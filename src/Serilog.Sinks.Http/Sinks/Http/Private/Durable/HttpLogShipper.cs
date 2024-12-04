﻿// Copyright 2015-2024 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Debugging;
using Serilog.Sinks.Http.Private.Time;

#if NET462
using System.Runtime.InteropServices;
#endif

namespace Serilog.Sinks.Http.Private.Durable
{
    public class HttpLogShipper : IDisposable
    {
        private readonly IHttpClient httpClient;
        private readonly string requestUri;
        private readonly IBufferFiles bufferFiles;
        private readonly long? logEventLimitBytes;
        private readonly int? logEventsInBatchLimit;
        private readonly long? batchSizeLimitBytes;
        private readonly IBatchFormatter batchFormatter;
        private readonly ExponentialBackoffConnectionSchedule connectionSchedule;
        private readonly bool flushOnClose;
        private readonly PortableTimer timer;
        private readonly CancellationTokenSource cts = new();
        private readonly object syncRoot = new();
        private bool isAllLogFilesSent;
        public bool IsAllLogFilesSent => isAllLogFilesSent;
        private bool disposed;

        public HttpLogShipper(
            IHttpClient httpClient,
            string requestUri,
            IBufferFiles bufferFiles,
            long? logEventLimitBytes,
            int? logEventsInBatchLimit,
            long? batchSizeLimitBytes,
            TimeSpan period,
            bool flushOnClose,
            IBatchFormatter batchFormatter)
        {
            if (logEventsInBatchLimit <= 0) throw new ArgumentException("logEventsInBatchLimit must be 1 or greater", nameof(logEventsInBatchLimit));

            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
            this.bufferFiles = bufferFiles ?? throw new ArgumentNullException(nameof(bufferFiles));
            this.logEventLimitBytes = logEventLimitBytes;
            this.logEventsInBatchLimit = logEventsInBatchLimit;
            this.batchSizeLimitBytes = batchSizeLimitBytes;
            this.batchFormatter = batchFormatter ?? throw new ArgumentNullException(nameof(batchFormatter));
            this.flushOnClose = flushOnClose;

            connectionSchedule = new ExponentialBackoffConnectionSchedule(period);
            timer = new PortableTimer(OnTick);

            SetTimer();
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;

                disposed = true;
            }

            if (flushOnClose)
            {
                // Dispose the timer and allow any ongoing operation to finish
                timer.Dispose();

                // Flush
                OnTick().GetAwaiter().GetResult();

                cts.Dispose();
                httpClient.Dispose();
            }
            else
            {
                // Cancel any ongoing operations
                cts.Cancel();

                // Dispose
                timer.Dispose();
                cts.Dispose();
                httpClient.Dispose();
            }
        }

        private void SetTimer()
        {
            // Note, called under syncRoot
            timer.Start(connectionSchedule.NextInterval);
        }

        private async Task OnTick()
        {
            try
            {
                Batch? batch = null;

                do
                {
                    using var bookmark = new BookmarkFile(bufferFiles.BookmarkFileName);
                    bookmark.TryReadBookmark(out var nextLineBeginsAtOffset, out var currentFile);

                    var fileSet = bufferFiles.Get();

                    if (currentFile == null || !System.IO.File.Exists(currentFile))
                    {
                        nextLineBeginsAtOffset = 0;
                        currentFile = fileSet.FirstOrDefault();
                    }

                    if (currentFile == null)
                    {
                        isAllLogFilesSent = true;
                        continue;
                    }

                    batch = BufferFileReader.Read(
                        currentFile,
                        ref nextLineBeginsAtOffset,
                        logEventLimitBytes,
                        logEventsInBatchLimit,
                        batchSizeLimitBytes,
                        cts.Token);

                    if (batch.LogEvents.Count > 0)
                    {
                        isAllLogFilesSent = false;
                        HttpResponseMessage response;

                        using (var contentStream = new MemoryStream())
                        using (var contentWriter = new StreamWriter(contentStream, Encoding.UTF8WithoutBom))
                        {
                            batchFormatter.Format(batch.LogEvents, contentWriter);

                            await contentWriter.FlushAsync();
                            contentStream.Position = 0;

                            if (contentStream.Length == 0)
                                continue;

                            response = await httpClient
                                .PostAsync(requestUri, contentStream, cts.Token)
                                .ConfigureAwait(false);
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            connectionSchedule.MarkSuccess();

                            bookmark.WriteBookmark(nextLineBeginsAtOffset, currentFile);
                        }
                        else
                        {
                            connectionSchedule.MarkFailure();

                            SelfLog.WriteLine(
                                "Received failed HTTP shipping result {0}: {1}",
                                response.StatusCode,
                                await response.Content.ReadAsStringAsync().ConfigureAwait(false));

                            break;
                        }
                    }
                    else
                    {
                        // For whatever reason, there's nothing waiting to send. This means we should try connecting
                        // again at the regular interval, so mark the attempt as successful.
                        connectionSchedule.MarkSuccess();

                        // Only advance the bookmark if no other process has the
                        // current file locked, and its length is as we found it.
                        if (fileSet.Length == 2
                            && fileSet.First() == currentFile
                            && IsUnlockedAtLength(currentFile, nextLineBeginsAtOffset))
                        {
                            bookmark.WriteBookmark(0, fileSet[1]);
                        }
                        else
                        {
                            isAllLogFilesSent = true;
                        }

                        if (fileSet.Length > 2)
                        {
                            // Once there's a third file waiting to ship, we do our
                            // best to move on, though a lock on the current file
                            // will delay this.
                            System.IO.File.Delete(fileSet[0]);
                        }
                    }
                } while (batch is { HasReachedLimit: true });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                SelfLog.WriteLine("Exception while emitting periodic batch from {0}: {1}", this, e);
                connectionSchedule.MarkFailure();
            }
            finally
            {
                lock (syncRoot)
                {
                    if (!disposed)
                    {
                        SetTimer();
                    }
                }
            }
        }

        private static bool IsUnlockedAtLength(string file, long maxLength)
        {
            try
            {
                using var fileStream = System.IO.File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                return fileStream.Length <= maxLength;
            }
#if NET462
            catch (IOException e)
            {
                var errorCode = Marshal.GetHRForException(e) & ((1 << 16) - 1);
                if (errorCode != 32 && errorCode != 33)
                {
                    SelfLog.WriteLine("Unexpected I/O exception while testing locked status of {0}: {1}", file, e);
                }
            }
#else
            catch (IOException)
            {
                // Where no HRESULT is available, assume IOExceptions indicate a locked file
            }
#endif
            catch (Exception e)
            {
                SelfLog.WriteLine("Unexpected exception while testing locked status of {0}: {1}", file, e);
            }

            return false;
        }
    }
}
