using System.Net;
using Brokers.Abstractions;

namespace Brokers.Exceptions;

public sealed class BrokerApiException : Exception
{
    private const int MaximumStoredBodyLength = 16 * 1024;

    public BrokerApiException(
        BrokerKind broker,
        HttpStatusCode statusCode,
        string responseBody,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"{broker} returned HTTP {(int)statusCode} ({statusCode}).", innerException)
    {
        Broker = broker;
        StatusCode = statusCode;
        ResponseBodyTruncated = responseBody.Length > MaximumStoredBodyLength;
        ResponseBody = ResponseBodyTruncated
            ? responseBody[..MaximumStoredBodyLength]
            : responseBody;
    }

    public BrokerKind Broker { get; }

    public HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }

    public bool ResponseBodyTruncated { get; }
}
