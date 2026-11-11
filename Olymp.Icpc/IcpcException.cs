using System.Globalization;

namespace Olymp.Icpc;

public enum IcpcStage { Regions, Contests, Authentication, Access, Teams, Members, Roster }
public enum IcpcFailure { Http, Cognito, AdditionalAuthentication, AccessDenied, Transport, Timeout, InvalidResponse, InvalidData, ResponseLimit }

/// <summary>Contains only safe diagnostic fields, never upstream messages, bodies or inner exceptions.</summary>
public sealed class IcpcException(IcpcStage stage, IcpcFailure failure, int? httpStatus = null, string? code = null)
    : Exception(string.Create(CultureInfo.InvariantCulture,
        $"ICPC failure: stage={stage}; failure={failure}; HTTP={SafeStatus(httpStatus)}; code={SafeCode(code)}."))
{
    public IcpcStage Stage { get; } = stage;
    public IcpcFailure Failure { get; } = failure;
    public int? HttpStatus { get; } = SafeStatus(httpStatus);
    public string? Code { get; } = SafeCode(code);

    private static int? SafeStatus(int? status) => status is >= 100 and <= 599 ? status : null;

    private static string? SafeCode(string? code) => code switch
    {
        "NotAuthorizedException" or "UserNotFoundException" or "UserNotConfirmedException"
            or "PasswordResetRequiredException" or "TooManyRequestsException" or "InvalidParameterException"
            or "InvalidPasswordException" or "ResourceNotFoundException" or "UnexpectedLambdaException"
            or "InvalidLambdaResponseException" or "UserLambdaValidationException" or "InternalErrorException"
            or "CodeMismatchException" or "ExpiredCodeException" or "ForbiddenException"
            or "InvalidUserPoolConfigurationException" or "InvalidEmailRoleAccessPolicyException"
            or "SMS_MFA" or "SOFTWARE_TOKEN_MFA" or "CUSTOM_CHALLENGE" or "NEW_PASSWORD_REQUIRED"
            or "MFA_SETUP" or "EMAIL_OTP" or "SMS_OTP" or "SELECT_MFA_TYPE" or "SELECT_CHALLENGE"
            or "PASSWORD_VERIFIER" => code,
        _ => null
    };
}
