namespace Olymp.WebApp.Components.Account;

public sealed class IdentityFeatures
{
    public const string SectionName = "Identity";
    public const string PasskeysPolicy = "PasskeysEnabled";

    public bool PasskeysEnabled { get; init; }
}
