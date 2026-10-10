using Esmbler.Kernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Esmbler.Api.Endpoints;

/// <summary>Maps kernel errors to RFC 9457 problem details with a stable code, target, and suggested fix.</summary>
public sealed class KernelExceptionHandler(ILogger<KernelExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        if (exception is KernelException kex)
        {
            problem = new ProblemDetails
            {
                Status = kex.Status,
                Title = kex.Message,
                Type = $"https://docs.esmbler.dev/errors/{kex.Code}",
            };
            problem.Extensions["code"] = kex.Code;
            if (kex.Target is not null)
            {
                problem.Extensions["target"] = kex.Target;
            }

            if (kex.Suggestion is not null)
            {
                problem.Extensions["suggestion"] = kex.Suggestion;
            }

            if (kex is ValidationFailedException vfe)
            {
                problem.Extensions["errors"] = vfe.Errors;
            }
            else if (kex.Details is not null)
            {
                problem.Extensions["details"] = kex.Details;
            }

            if (kex.Status >= 500)
            {
                logger.LogWarning(exception, "Request failed with {Code}", kex.Code);
            }
        }
        else if (exception is BadHttpRequestException bad)
        {
            problem = new ProblemDetails { Status = 400, Title = "The request could not be read.", Detail = bad.Message };
            problem.Extensions["code"] = "bad_request";
        }
        else
        {
            logger.LogError(exception, "Unhandled error");
            problem = new ProblemDetails { Status = 500, Title = "Something went wrong on our side. Try again; if it keeps happening, contact support with the trace ID." };
            problem.Extensions["code"] = "internal_error";
        }

        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = problem.Status ?? 500;
        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }
}
