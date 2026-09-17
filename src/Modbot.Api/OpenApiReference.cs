using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Modbot.Api.Auth;
using Modbot.Core;
using Modbot.Core.Data.Entities;

namespace Modbot.Api;

/// <summary>
/// What the OpenAPI document says beyond what each endpoint declares: who may call what, how to
/// sign in, what an error looks like, and the order and wording of the sections.
/// </summary>
/// <remarks>
/// <para>
/// The documentation site's API reference is made from this document (docs/openapi/modbot.json,
/// written on every build of Modbot.Server). Everything here is read from the same metadata the
/// server enforces -- <c>RequiresFlag</c>, <c>AllowAnonymous</c> and the API key rules in
/// <see cref="ApiKeyAuthentication.KeysMayNotUse"/> -- so the reference cannot claim an endpoint
/// takes a key that the server refuses.
/// </para>
/// <para>
/// Endpoints that are not for other programs are left out where they are mapped, with
/// <c>ExcludeFromDescription</c>: the setup wizard, the companion's protocol and pairing, the
/// member-facing Discord link page, and the sync pacing internals.
/// </para>
/// </remarks>
internal static class OpenApiReference
{
    public const string SessionScheme = "session";
    public const string ApiKeyScheme = "apiKey";
    public const string ErrorSchema = "Error";

    /// <summary>The live event WebSocket, which authenticates inside the handler.</summary>
    private const string EventSocketOperation = "EventSocket";

    /// <summary>The sections, in the order the reference lists them, with one line each.</summary>
    private static readonly (string Name, string Description)[] Tags =
    [
        ("Version", "The server's release and API version."),
        ("Auth", "Signing in and out, and your own account. API keys can only use GET /api/auth/me here."),
        ("Users", "Staff accounts, invite links and password reset links."),
        ("Roles", "Roles and the permissions each one gives."),
        ("API keys", "Keys that let a program use this API as the person who made them."),
        ("Events", "Every new event as it happens, over a WebSocket or by long polling."),
        ("Webhooks", "Addresses Modbot sends new events to, signed with a secret."),
        ("Members", "The group's member list and ban list, as Modbot last read them."),
        ("VRChat users", "What Modbot has stored about one VRChat user."),
        ("Live", "The group's open instances right now."),
        ("Calendar", "Planned events, where each is published, and the calendar feed."),
        ("Places", "One world or one instance."),
        ("Audit", "The audit log: who did what to whom, and when."),
        ("Case files", "The write-up of each ban."),
        ("Evidence", "Screenshots and video attached to case files."),
        ("Reviews", "Reviews that open when a moderator's actions look unusual."),
        ("Repeat offenders", "People acted on more than once."),
        ("Moderation", "Flags raised by moderation rules."),
        ("Discord", "The Discord server's channels, roles and members, and where events are posted."),
        ("Discord account link", "Links between members' Discord and VRChat accounts."),
        ("Chat", "Questions answered by the AI model, using only what you can see."),
        ("Insights", "AI-written summaries of the group's own numbers."),
        ("Alerts", "Times something ran far outside this deployment's own normal."),
        ("Analytics", "Charts and daily totals."),
        ("Health", "Whether Modbot is reaching VRChat."),
        ("Settings", "Deployment settings: storage, retention, email, evidence, ban reasons and more."),
        ("AI settings", "The AI provider, Chat, moderation rules, insights, alerts and spend limits."),
    ];

    public static OpenApiOptions AddModbotReference(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            DescribeDocument(document);
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            DescribeOperation(operation, context);
            return Task.CompletedTask;
        });

        return options;
    }

    private static void DescribeDocument(OpenApiDocument document)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Modbot API",
            // The API version, not the calendar release: it changes only on a breaking change, so
            // the committed document does not change with every release.
            Version = ModbotVersion.Api.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Description =
                "Cached VRChat group data, moderation history and analytics for a single group.\n\n"
                + "This is one self-hosted deployment's API. There is no central Modbot service and "
                + "no shared endpoint: you are talking to somebody's own server, at the address "
                + "they host it on.\n\n"
                + "**Signing in.** A program sends an API key as `Authorization: Bearer mbk_...`. "
                + "The key acts as the person who made it, with the permissions chosen for the key "
                + "and never more than that person holds now. The web app uses the "
                + "`modbot.session` cookie instead. Both are refused for an account that is "
                + "disabled or has not linked its VRChat account.\n\n"
                + "**Errors** usually carry a JSON body with one field, `error`, saying what went "
                + "wrong in a sentence.\n\n"
                + $"API version {ModbotVersion.Api} (oldest still supported: "
                + $"{ModbotVersion.ApiMinimum}). The API version is a plain number, separate from "
                + "the release version, and goes up only on a breaking change.",
            License = new OpenApiLicense
            {
                Name = "AGPL-3.0",
                Url = new Uri("https://www.gnu.org/licenses/agpl-3.0.html"),
            },
        };

        document.Servers =
        [
            new OpenApiServer
            {
                Url = "https://{address}",
                Description = "Your Modbot",
                Variables = new Dictionary<string, OpenApiServerVariable>
                {
                    ["address"] = new()
                    {
                        Default = "modbot.example.com",
                        Description = "The address your Modbot is hosted at.",
                    },
                },
            },
        ];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "mbk_...",
            Description = "An API key, made on Settings, API keys. Every key starts with `mbk_`.",
        };
        document.Components.SecuritySchemes[SessionScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = ModbotAuth.CookieName,
            Description = "The web app's session, set by POST /api/auth/login.",
        };

        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[ErrorSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "What went wrong. Some errors have no body at all.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["error"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "One sentence saying what went wrong.",
                    Examples = [JsonValue.Create("That username is already taken.")],
                },
            },
        };

        // Sections in a fixed order, keeping any tag an endpoint uses that the list above lacks.
        var used = document.Tags?.Select(t => t.Name).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var ordered = new List<OpenApiTag>();
        foreach (var (name, description) in Tags)
        {
            if (used.Remove(name))
                ordered.Add(Tag(name, description));
        }

        ordered.AddRange(used.Order(StringComparer.Ordinal).Select(name => Tag(name, null)));
        document.Tags = new HashSet<OpenApiTag>(ordered);
    }

    /// <remarks>
    /// <c>x-displayName</c> is the section title as written. Without it, readers of the document
    /// such as the docs site make one up from the name, and "API keys" comes out as "A P I keys".
    /// </remarks>
    private static OpenApiTag Tag(string name, string? description) => new()
    {
        Name = name,
        Description = description,
        Extensions = new Dictionary<string, IOpenApiExtension>
        {
            ["x-displayName"] = new JsonNodeExtension(JsonValue.Create(name)),
        },
    };

    private static void DescribeOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        var authorized = !anonymous && metadata.OfType<IAuthorizeData>().Any();

        var required = metadata.OfType<RequiresFlagAttribute>()
            .Aggregate(ModbotPermissions.None, (all, attribute) => all | attribute.Flags);

        if (authorized)
        {
            var requirements = new List<OpenApiSecurityRequirement>
            {
                Requirement(SessionScheme, context.Document),
            };

            if (!KeysRefused(context))
                requirements.Insert(0, Requirement(ApiKeyScheme, context.Document));

            operation.Security = requirements;

            AddResponse(operation, StatusCodes.Status401Unauthorized, "Not signed in, or the API key is not valid.");
        }
        else if (string.Equals(operation.OperationId, EventSocketOperation, StringComparison.Ordinal))
        {
            // Mapped AllowAnonymous because it checks its own credentials once the socket opens: an
            // API key in the Authorization header, or a one-use ticket in the query string.
            operation.Security = [Requirement(ApiKeyScheme, context.Document), new OpenApiSecurityRequirement()];
        }
        else
        {
            operation.Security = [];
        }

        if (required != ModbotPermissions.None)
        {
            var names = PermissionCatalog.NamesOf(required).Select(n => $"`{n}`");
            var sentence = $"Needs the {string.Join(" and ", names)} permission{(PermissionCatalog.NamesOf(required).Count > 1 ? "s" : "")}.";
            operation.Description = string.IsNullOrWhiteSpace(operation.Description)
                ? sentence
                : operation.Description + "\n\n" + sentence;

            AddResponse(operation, StatusCodes.Status403Forbidden, "Signed in, but without a permission this needs.");
        }

        if (operation.Responses is null)
            return;

        foreach (var (status, response) in operation.Responses)
        {
            if (response is not OpenApiResponse concrete || concrete.Content is { Count: > 0 })
                continue;

            if (status is "400" or "404" or "409" or "413" or "415" or "429" or "503")
            {
                concrete.Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchemaReference(ErrorSchema, context.Document),
                    },
                };
            }
        }
    }

    private static OpenApiSecurityRequirement Requirement(string scheme, OpenApiDocument? document)
        => new() { [new OpenApiSecuritySchemeReference(scheme, document)] = [] };

    private static void AddResponse(OpenApiOperation operation, int status, string description)
    {
        operation.Responses ??= new OpenApiResponses();
        var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!operation.Responses.ContainsKey(key))
            operation.Responses[key] = new OpenApiResponse { Description = description };
    }

    /// <summary>Asks the key handler's own rule whether a key would be refused on this route.</summary>
    private static bool KeysRefused(OpenApiOperationTransformerContext context)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = context.Description.HttpMethod ?? HttpMethods.Get;
        http.Request.Path = "/" + (context.Description.RelativePath ?? string.Empty).TrimStart('/');

        return ApiKeyAuthentication.KeysMayNotUse(http.Request);
    }
}
