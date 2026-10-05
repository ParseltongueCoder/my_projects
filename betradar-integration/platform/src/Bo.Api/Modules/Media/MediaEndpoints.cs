using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bo.Api.Infrastructure;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Media;

/// <summary>
/// Logos, icons and flags (docs/06 §2.5.6). Until object storage + CDN exist the bytes live in <c>bo.media_blob</c> and
/// are served from <c>/api/media/{id}</c> with immutable caching (content never changes for an id).
/// </summary>
public static partial class MediaEndpoints
{
    public const int MaxBytes = 512 * 1024;

    public sealed record LinkRequest(string EntityType, long EntityId, string Role, Guid? MediaId, bool Platform = false);

    private static readonly string[] EntityTypes = ["sport", "category", "tournament", "competitor", "player", "event"];
    private static readonly string[] Roles = ["icon", "flag", "logo", "banner"];

    [GeneratedRegex(@"<\s*(script|foreignObject|iframe|embed|object)\b|\son[a-z]+\s*=|javascript:|(xlink:)?href\s*=\s*[""']\s*(https?:|//|data:)", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSvg();

    public static RouteGroupBuilder MapMedia(this RouteGroupBuilder api)
    {
        api.MapPost("/media", async (TenantContext t, BoDb db, IFormFile file, CancellationToken ct) =>
        {
            t.Require("cat.media.upload");
            if (t.OperatorId is null)
            {
                t.RequirePlatform();
            }
            if (file.Length is 0 or > MaxBytes)
            {
                throw BoProblem.Invalid("BAD_FILE", $"Images must be 1 byte to {MaxBytes / 1024} KB");
            }
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            var mime = Sniff(bytes) ?? throw BoProblem.Invalid("BAD_FILE", "Only PNG, JPEG, WebP or SVG images");
            if (mime == "image/svg+xml" && UnsafeSvg().IsMatch(Encoding.UTF8.GetString(bytes)))
            {
                throw BoProblem.Invalid("UNSAFE_SVG", "SVG with scripts, event handlers or external references is not allowed");
            }
            var sha = SHA256.HashData(bytes);
            return await db.TenantAsync(t, async (conn, tx) =>
            {
                // Same image uploaded again by the same tenant: reuse it.
                var existing = await conn.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM bo.media_asset WHERE operator_id IS NOT DISTINCT FROM @op AND sha256 = @sha", new { op = t.OperatorId, sha }, tx);
                var id = existing ?? await conn.ExecuteScalarAsync<Guid>("""
                    INSERT INTO bo.media_asset (operator_id, sha256, mime, size_bytes, file_name, uploaded_by)
                    VALUES (@op, @sha, @mime, @size, @name, @user) RETURNING id
                    """, new { op = t.OperatorId, sha, mime, size = bytes.Length, name = Path.GetFileName(file.FileName), user = t.UserId }, tx);
                if (existing is null)
                {
                    await conn.ExecuteAsync("INSERT INTO bo.media_blob (media_id, content) VALUES (@id, @bytes)", new { id, bytes }, tx);
                    await Audit.WriteAsync(conn, tx, t, "cat.media.uploaded", "media", id.ToString(), after: new { mime, size = bytes.Length, file.FileName });
                }
                return Results.Ok(new { id, url = $"/api/media/{id}", mime, size = bytes.Length });
            }, ct);
        }).DisableAntiforgery();

        api.MapPut("/media/links", (TenantContext t, BoDb db, LinkRequest body, CancellationToken ct) =>
        {
            t.Require("cat.edit");
            if (!EntityTypes.Contains(body.EntityType) || !Roles.Contains(body.Role))
            {
                throw BoProblem.Invalid("BAD_LINK", "Unknown entity type or role");
            }
            long? operatorId = body.Platform ? null : t.RequireOperator();
            if (body.Platform)
            {
                t.RequirePlatform();
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                if (!await conn.ExecuteScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM sb.{body.EntityType} WHERE id = @EntityId)", body, tx))
                {
                    throw BoProblem.NotFound(body.EntityType);
                }
                if (body.MediaId is { } mediaId)
                {
                    if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM bo.media_asset WHERE id = @mediaId)", new { mediaId }, tx))
                    {
                        throw BoProblem.NotFound("Image");
                    }
                    await conn.ExecuteAsync("""
                        INSERT INTO bo.media_link (operator_id, entity_type, entity_id, role, media_id, updated_by)
                        VALUES (@operatorId, @EntityType, @EntityId, @Role, @mediaId, @user)
                        ON CONFLICT (operator_id, entity_type, entity_id, role) DO UPDATE SET media_id = EXCLUDED.media_id,
                          updated_by = EXCLUDED.updated_by, updated_at = now()
                        """, new { operatorId, body.EntityType, body.EntityId, body.Role, mediaId, user = t.UserId }, tx);
                }
                else
                {
                    await conn.ExecuteAsync("""
                        DELETE FROM bo.media_link WHERE operator_id IS NOT DISTINCT FROM @operatorId AND entity_type = @EntityType
                          AND entity_id = @EntityId AND role = @Role
                        """, new { operatorId, body.EntityType, body.EntityId, body.Role }, tx);
                }
                await Audit.WriteAsync(conn, tx, t, "cat.media.linked", body.EntityType, body.EntityId.ToString(), after: body, operatorId: operatorId);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId?.ToString() ?? "platform"}.cat", new { body.EntityType, body.EntityId });
                return Results.NoContent();
            }, ct);
        });

        return api;
    }

    /// <summary>Public image endpoint (logos are public); the id is random and content is immutable.</summary>
    public static IEndpointRouteBuilder MapPublicMedia(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/media/{id:guid}", async (NpgsqlDataSource db, Guid id, HttpContext http, CancellationToken ct) =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<(string Mime, byte[] Content)>("""
                SELECT a.mime, b.content FROM bo.media_asset a JOIN bo.media_blob b ON b.media_id = a.id WHERE a.id = @id
                """, new { id });
            if (row == default)
            {
                return Results.NotFound();
            }
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // An SVG opened directly must not run anything.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.Bytes(row.Content, row.Mime);
        }).AllowAnonymous();
        return app;
    }

    /// <summary>File type from the bytes, not the client's claim.</summary>
    public static string? Sniff(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            return "image/png";
        }
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP")
        {
            return "image/webp";
        }
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).TrimStart('﻿', ' ', '\n', '\r', '\t');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.Ordinal) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            ? "image/svg+xml"
            : null;
    }
}
