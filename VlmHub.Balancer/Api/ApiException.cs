using System.Net;

namespace VlmHub.Balancer.Api;

public class VlmApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public VlmApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

public sealed class VlmServerBusyException : VlmApiException
{
    public VlmServerBusyException(string message, HttpStatusCode? statusCode = null)
        : base(message, statusCode)
    {
    }
}

public sealed class VlmServerTransientException : VlmApiException
{
    public VlmServerTransientException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, statusCode, innerException)
    {
    }
}

public sealed class VlmRequestException : VlmApiException
{
    public VlmRequestException(string message, HttpStatusCode? statusCode = null)
        : base(message, statusCode)
    {
    }
}

public sealed class VlmModelUnavailableException : VlmApiException
{
    public string Model { get; }

    public VlmModelUnavailableException(
        string model,
        string message,
        HttpStatusCode? statusCode = null)
        : base(message, statusCode)
    {
        Model = model;
    }
}

public sealed class VlmProcessingTechnicalException : VlmApiException
{
    public VlmProcessingTechnicalException(string message, HttpStatusCode? statusCode = null)
        : base(message, statusCode)
    {
    }
}

public sealed class VlmServerRecoveryException : VlmApiException
{
    public VlmServerRecoveryException(string message, HttpStatusCode? statusCode = null)
        : base(message, statusCode)
    {
    }
}
