using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Olymp.WebApp.Services;

public sealed class ClientConnection(IHttpContextAccessor http) : CircuitHandler
{
    public string? RemoteIpAddress { get; private set; }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        string? address = http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        if (address is not null) RemoteIpAddress = address;

        return Task.CompletedTask;
    }
}
