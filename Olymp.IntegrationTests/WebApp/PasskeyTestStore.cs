using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.IntegrationTests.WebApp;

// Passkey UI tests keep credentials in memory; the existing database schema stays unchanged.
internal sealed class PasskeyTestData
{
    public List<UserPasskeyInfo> Passkeys { get; } = [];
}

internal sealed class PasskeyTestStore(OlympContext context, PasskeyTestData data)
    : UserStore<User, IdentityRole<int>, OlympContext, int>(context)
{
    public override Task<IList<UserPasskeyInfo>> GetPasskeysAsync(User user, CancellationToken cancellationToken = default) =>
        Task.FromResult<IList<UserPasskeyInfo>>(data.Passkeys);

    public override Task<UserPasskeyInfo?> FindPasskeyAsync(User user, byte[] credentialId, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Passkeys.SingleOrDefault(passkey => passkey.CredentialId.SequenceEqual(credentialId)));

    public override Task AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey, CancellationToken cancellationToken = default)
    {
        data.Passkeys.RemoveAll(existing => existing.CredentialId.SequenceEqual(passkey.CredentialId));
        data.Passkeys.Add(passkey);
        return Task.CompletedTask;
    }

    public override Task RemovePasskeyAsync(User user, byte[] credentialId, CancellationToken cancellationToken = default)
    {
        data.Passkeys.RemoveAll(passkey => passkey.CredentialId.SequenceEqual(credentialId));
        return Task.CompletedTask;
    }
}
