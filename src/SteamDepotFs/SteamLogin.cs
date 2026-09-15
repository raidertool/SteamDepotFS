using SteamKit2;
using SteamKit2.Authentication;

internal static class SteamLogin
{
    public static async Task<SteamUser.LogOnDetails> CreateLogOnDetailsAsync(
        SteamCredentials credentials,
        Func<AuthSessionDetails, CancellationToken, Task<(string AccountName, string RefreshToken)>> authenticate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountName = credentials.Username;
        var refreshToken = credentials.AccessToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrEmpty(credentials.Password))
            {
                throw new ArgumentException("Steam login requires a username and password, or a username and access token.");
            }

            var details = new AuthSessionDetails
            {
                Username = accountName,
                Password = credentials.Password,
                IsPersistentSession = credentials.RememberPassword,
                DeviceFriendlyName = "SteamDepotFS",
                Authenticator = new SteamGuardCodes(credentials.AuthCode, credentials.TwoFactorCode)
            };
            (accountName, refreshToken) = await authenticate(details, cancellationToken).WaitAsync(cancellationToken);
        }

        return new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            LoginID = credentials.LoginId,
            ShouldRememberPassword = credentials.RememberPassword
        };
    }
}

internal sealed class SteamGuardCodes(string? emailCode, string? deviceCode) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        => GetCodeAsync(deviceCode, "--two-factor-code / STEAM_TWO_FACTOR_CODE", previousCodeWasIncorrect);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => GetCodeAsync(emailCode, "--auth-code / STEAM_AUTH_CODE", previousCodeWasIncorrect);

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

    private static Task<string> GetCodeAsync(string? code, string option, bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
        {
            throw new UnauthorizedAccessException($"Steam rejected the supplied Guard code. Retry with a new {option}.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new UnauthorizedAccessException($"Steam Guard requires {option}. Login does not prompt for input.");
        }

        return Task.FromResult(code);
    }
}
