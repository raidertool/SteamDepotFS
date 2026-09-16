using SteamKit2.Authentication;
using Xunit;

namespace SteamDepotFs.Tests;

public sealed class SteamLoginTests
{
    private static SteamCredentials Credentials => new("account", "password", null, null, null, 42, false);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stale-password")]
    public async Task SuppliedToken_BypassesPasswordAuthentication(string? password)
    {
        var credentials = Credentials with
        {
            Password = password,
            AccessToken = "supplied-token",
            AuthCode = "old-email-code",
            TwoFactorCode = "old-device-code"
        };

        var result = await SteamLogin.CreateLogOnDetailsAsync(credentials,
            (_, _) => throw new InvalidOperationException("Token login must not exchange credentials."),
            CancellationToken.None);

        Assert.Equal("account", result.Username);
        Assert.Equal("supplied-token", result.AccessToken);
        Assert.Null(result.Password);
        Assert.Null(result.AuthCode);
        Assert.Null(result.TwoFactorCode);
        Assert.Equal(42U, result.LoginID);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordAuthentication_UsesReturnedAccountAndRefreshToken(bool rememberPassword)
    {
        using var cancellation = new CancellationTokenSource();
        var credentials = Credentials with { RememberPassword = rememberPassword };
        var result = await SteamLogin.CreateLogOnDetailsAsync(credentials, (details, token) =>
        {
            Assert.Equal("account", details.Username);
            Assert.Equal("password", details.Password);
            Assert.Equal(rememberPassword, details.IsPersistentSession);
            Assert.IsType<SteamGuardCodes>(details.Authenticator);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(("canonical-account", "fresh-refresh-token"));
        }, cancellation.Token);

        Assert.Equal("canonical-account", result.Username);
        Assert.Equal("fresh-refresh-token", result.AccessToken);
        Assert.Null(result.Password);
        Assert.Equal(rememberPassword, result.ShouldRememberPassword);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotFallBackToPasswordLogon()
    {
        var rejected = new UnauthorizedAccessException("Authentication rejected.");
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SteamLogin.CreateLogOnDetailsAsync(Credentials, (_, _) => throw rejected, CancellationToken.None));

        Assert.Same(rejected, error);
    }

    [Fact]
    public async Task CancelledAuthentication_StopsWaitingForExchange()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = SteamLogin.CreateLogOnDetailsAsync(Credentials, (_, _) => pending.Task, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("account", null)]
    [InlineData("account", "")]
    public async Task MissingCredentials_FailBeforeAuthentication(string? username, string? password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SteamLogin.CreateLogOnDetailsAsync(
            Credentials with { Username = username, Password = password },
            (_, _) => throw new InvalidOperationException("Incomplete credentials must not reach Steam."),
            CancellationToken.None));
    }

    [Fact]
    public async Task GuardCodes_UseConfiguredCodesInsteadOfDeviceApproval()
    {
        var guard = new SteamGuardCodes("email-code", "device-code");

        Assert.False(await guard.AcceptDeviceConfirmationAsync());
        Assert.Equal("email-code", await guard.GetEmailCodeAsync("address", false));
        Assert.Equal("device-code", await guard.GetDeviceCodeAsync(false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingGuardCodes_FailWithoutInteractiveInput(string? code)
    {
        var guard = new SteamGuardCodes(code, code);

        var emailError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.GetEmailCodeAsync("address", false));
        var deviceError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.GetDeviceCodeAsync(false));

        Assert.Contains("STEAM_AUTH_CODE", emailError.Message);
        Assert.Contains("STEAM_TWO_FACTOR_CODE", deviceError.Message);
        Assert.False(await guard.AcceptDeviceConfirmationAsync());
    }

    [Fact]
    public async Task RejectedGuardCodes_FailInsteadOfRetryingTheSameCode()
    {
        var guard = new SteamGuardCodes("email-code", "device-code");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.GetEmailCodeAsync("address", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.GetDeviceCodeAsync(true));
    }
}
