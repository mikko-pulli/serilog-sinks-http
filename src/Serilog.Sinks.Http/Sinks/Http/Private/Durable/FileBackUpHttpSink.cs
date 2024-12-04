// Copyright 2015-2024 Serilog Contributors
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
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.Http.Private.IO;
using Serilog.Sinks.Http.Private.NonDurable;
using Serilog.Sinks.Http.Private.Time;

namespace Serilog.Sinks.Http.Private.Durable;

public class FileBackUpHttpSink : ILogEventSink, IDisposable
{
    private readonly HttpLogShipper shipper;

    private readonly string requestUri;
    private readonly long? logEventLimitBytes;
    private readonly int? logEventsInBatchLimit;
    private readonly long? batchSizeLimitBytes;
    private readonly bool flushOnClose;
    private readonly ITextFormatter textFormatter;
    private readonly IBatchFormatter batchFormatter;
    private readonly IHttpClient httpClient;
    private readonly ExponentialBackoffConnectionSchedule connectionSchedule;
    private readonly PortableTimer timer;
    private readonly LogEventQueue queue;
    private readonly string fallbackFilePath;
    private readonly CancellationTokenSource cts = new();
    private readonly object syncRoot = new();
    private bool disposed;

    public FileBackUpHttpSink(
        string requestUri,
        long? queueLimitBytes,
        string bufferBaseFileName,
        long? logEventLimitBytes,
        int? logEventsInBatchLimit,
        long? batchSizeLimitBytes,
        TimeSpan period,
        bool flushOnClose,
        ITextFormatter textFormatter,
        IBatchFormatter batchFormatter,
        IHttpClient httpClient)
    {
        this.requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        this.logEventLimitBytes = logEventLimitBytes;
        this.logEventsInBatchLimit = logEventsInBatchLimit;
        this.batchSizeLimitBytes = batchSizeLimitBytes;
        this.flushOnClose = flushOnClose;
        this.textFormatter = textFormatter ?? throw new ArgumentNullException(nameof(textFormatter));
        this.batchFormatter = batchFormatter ?? throw new ArgumentNullException(nameof(batchFormatter));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        connectionSchedule = new ExponentialBackoffConnectionSchedule(period);
        timer = new PortableTimer(OnTick);
        queue = new LogEventQueue(queueLimitBytes);
        fallbackFilePath = bufferBaseFileName;

        SetTimer();

        var logShipperTimePeriod = TimeSpan.FromSeconds(5);
        shipper = new HttpLogShipper(
            httpClient,
            requestUri,
            new TimeRolledBufferFiles(new DirectoryService(), bufferBaseFileName),
            logEventLimitBytes,
            logEventsInBatchLimit,
            batchSizeLimitBytes,
            logShipperTimePeriod,
            flushOnClose,
            batchFormatter);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) throw new ArgumentNullException(nameof(logEvent));

        var writer = new StringWriter();
        textFormatter.Format(logEvent, writer);
        var formattedLogEvent = writer.ToString();

        if (ByteSize.From(formattedLogEvent) > logEventLimitBytes)
        {
            SelfLog.WriteLine(
                "Log event exceeds the size limit of {0} bytes set for this sink and will be dropped; data: {1}",
                logEventLimitBytes,
                formattedLogEvent);
            return;
        }

        if (!shipper.IsAllLogFilesSent && formattedLogEvent != null)
        {
            WriteBatchToFile(formattedLogEvent);
            return;
        }
        else if (formattedLogEvent != null)
        { 
            var result = queue.TryEnqueue(formattedLogEvent);
            if (result == LogEventQueue.EnqueueResult.QueueFull)
            {
                SelfLog.WriteLine("Queue has reached its limit and the log event will be dropped; data: {0}", formattedLogEvent);
            }
        }
    }

    public void Dispose()
    {
        shipper.Dispose();
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
        NonDurable.Batch? batch = null;
        try
        {
            do
            {
                batch = LogEventQueueReader.Read(queue, logEventsInBatchLimit, batchSizeLimitBytes);

                if (batch.LogEvents.Count > 0)
                {
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
                    }
                    else
                    {
                        connectionSchedule.MarkFailure();
                        WriteBatchToFile(batch);

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        SelfLog.WriteLine("Received failed HTTP shipping result {0}: {1}", response.StatusCode, body);
                        break;
                    }
                }
                else
                {
                    // For whatever reason, there's nothing waiting to be sent. This means we should try connecting
                    // again at the regular interval, so mark the attempt as successful.
                    connectionSchedule.MarkSuccess();
                }
            } while (batch.HasReachedLimit);
        }
        catch (OperationCanceledException)
        {
            SelfLog.WriteLine("Operation cancelled while emitting periodic batch from {0}", this);
            if (batch != null)
            {
                WriteBatchToFile(batch);
            }
            connectionSchedule.MarkFailure();
        }
        catch (Exception e)
        {
            SelfLog.WriteLine("Exception while emitting periodic batch from {0}: {1}", this, e);
            if (batch != null)
            {
                WriteBatchToFile(batch);
            }
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

    private void WriteBatchToFile(NonDurable.Batch batch)
    {
        WriteBatchToFile(batch.LogEvents);
    }

    private void WriteBatchToFile(List<string> logEvents)
    {
        try
        {
            using var fileStream = new FileStream($"{fallbackFilePath}-{DateTime.UtcNow:yyyyMMdd}.json", FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fileStream, Encoding.UTF8WithoutBom);
            foreach (var formattedLogEvent in logEvents)
            {
                writer.Write(formattedLogEvent);
            }
            writer.Flush();
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("Failed to write log events to fallback file: {0}", ex);
        }
    }

    private void WriteBatchToFile(string logEvent)
    {
        try
        {
            using var fileStream = new FileStream($"{fallbackFilePath}-{DateTime.UtcNow:yyyyMMdd}.json", FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fileStream, Encoding.UTF8WithoutBom);
            writer.Write(logEvent);
            writer.Flush();
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("Failed to write log events to fallback file: {0}", ex);
        }
    }
}
