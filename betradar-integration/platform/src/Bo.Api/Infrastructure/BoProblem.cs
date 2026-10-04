namespace Bo.Api.Infrastructure;

/// <summary>A domain error with a stable machine-readable code (RFC 9457 ProblemDetails + <c>code</c>, docs/08 §1.6).</summary>
public sealed class BoProblem(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static BoProblem Forbidden(string code, string message) => new(StatusCodes.Status403Forbidden, code, message);
    public static BoProblem NotFound(string what) => new(StatusCodes.Status404NotFound, "NOT_FOUND", $"{what} not found");
    public static BoProblem Invalid(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);
    public static BoProblem Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
}

internal sealed class BoProblemMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BoProblem p) when (!context.Response.HasStarted)
        {
            await Write(context, p.Status, p.Code, p.Message);
        }
        catch (Npgsql.PostgresException e) when (!context.Response.HasStarted && e.SqlState is "23505" or "42501")
        {
            // Unique key → duplicate; RLS "new row violates row-level security policy" → another tenant's data.
            await (e.SqlState == "23505"
                ? Write(context, StatusCodes.Status409Conflict, "DUPLICATE", "A record with the same key already exists")
                : Write(context, StatusCodes.Status403Forbidden, "TENANT_VIOLATION", "Not allowed for this operator"));
        }
    }

    private static Task Write(HttpContext context, int status, string code, string message) =>
        Results.Problem(statusCode: status, title: message, extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context);
}
