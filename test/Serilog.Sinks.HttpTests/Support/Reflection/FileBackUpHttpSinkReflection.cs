using Serilog.Core;
using Serilog.Sinks.Http;
using Serilog.Sinks.Http.Private.Durable;
using Serilog.Sinks.Http.Private.IO;

namespace Serilog.Support.Reflection;

public class FileBackUpHttpSinkReflection
{
    private readonly FileBackUpHttpSink sink;

    public FileBackUpHttpSinkReflection(Logger logger) => sink = logger.GetSink<FileBackUpHttpSink>();

    public FileBackUpHttpSinkReflection SetRequestUri(string requestUri)
    {
        sink.SetNonPublicInstanceField("requestUri", requestUri);
        sink
           .GetNonPublicInstanceField<HttpLogShipper>("shipper")
           .SetNonPublicInstanceField("requestUri", requestUri);
        return this;
    }

    public FileBackUpHttpSinkReflection SetBufferBaseFileName(string bufferBaseFileName, bool isAllSentAtBeginning = true)
    {
        // Update shipper
        var shipper = this.sink.GetNonPublicInstanceField<HttpLogShipper>("shipper");
        var timeRolledBufferFiles = new TimeRolledBufferFiles(new DirectoryService(), bufferBaseFileName);
        shipper.SetNonPublicInstanceField("bufferFiles", timeRolledBufferFiles);
        shipper.SetNonPublicInstanceField("isAllLogFilesSent", isAllSentAtBeginning);

        // Update file sink
        sink.SetNonPublicInstanceField("fallbackFilePath", bufferBaseFileName);

        return this;
    }

    public FileBackUpHttpSinkReflection SetHttpClient(IHttpClient httpClient)
    {
        sink.SetNonPublicInstanceField("httpClient", httpClient);
        sink
            .GetNonPublicInstanceField<HttpLogShipper>("shipper")
            .SetNonPublicInstanceField("httpClient", httpClient);

        return this;
    }
}
