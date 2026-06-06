using OpenCodex.Api.DTOs.AdminAuth;

namespace OpenCodex.Api.Tests.DTOs.AdminAuth;

public sealed class AdminLoginRequestTests
{
    [Fact]
    public void FromTrimsStringsAndKeepsPrimitiveCompatibility()
    {
        var request = AdminLoginRequest.From(new Dictionary<string, object?>
        {
            ["username"] = " admin ",
            ["password"] = 1234
        });

        Assert.Equal("admin", request.Username);
        Assert.Equal("1234", request.Password);
    }

    [Fact]
    public void FromDefaultsMissingOrComplexValuesToEmptyStrings()
    {
        var request = AdminLoginRequest.From(new Dictionary<string, object?>
        {
            ["username"] = new Dictionary<string, object?>()
        });

        Assert.Equal(string.Empty, request.Username);
        Assert.Equal(string.Empty, request.Password);
    }
}
