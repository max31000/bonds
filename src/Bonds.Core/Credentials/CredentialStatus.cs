namespace Bonds.Core.Credentials;

/// <summary>Статусы секрета в <c>user_credentials.status</c> (+ <see cref="Missing"/> — виртуальный, только в API).</summary>
public static class CredentialStatus
{
    public const string Ok = "ok";
    public const string Invalid = "invalid";
    public const string Unverified = "unverified";

    /// <summary>Записи в БД нет — приходит только в ответе API по провайдерам реестра, которых пользователь не задал.</summary>
    public const string Missing = "missing";
}
