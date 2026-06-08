namespace OpenCodex.CoreBase.Services;

public static class AdminAuthErrorCodes
{
    public const int InvalidCredentials = 401001;
}

public static class AdminChannelDiagnosticsErrorCodes
{
    public const int Validation = 400301;
}

public static class AdminApiKeyErrorCodes
{
    public const int Validation = 400201;
    public const int NotFound = 404201;
}

public static class AdminUserErrorCodes
{
    public const int Validation = 400101;
    public const int NotFound = 404101;
}

public static class AdminConfigErrorCodes
{
    public const int Validation = 40001;
}

public static class AdminObservabilityErrorCodes
{
    public const int NotFound = 40404;
}
