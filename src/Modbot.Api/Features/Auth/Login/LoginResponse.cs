using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Auth.Login;

/// <param name="Id">The staff account's id.</param>
/// <param name="Username">As the operator typed it when the account was created.</param>
/// <param name="Permissions">
/// The caller's bitfield, so the SPA can hide what it cannot do. The server re-checks every
/// request regardless -- this is for the UI, never for enforcement.
/// </param>
public sealed record LoginResponse(Guid Id, string Username, ModbotPermissions Permissions);
