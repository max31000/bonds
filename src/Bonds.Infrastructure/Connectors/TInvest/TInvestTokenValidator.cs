using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Реализация <see cref="ITInvestTokenValidator"/> — строит одноразовый <c>InvestApiClient</c>
/// напрямую из переданного токена (в обход <c>ITInvestTokenProvider</c>/БД, см. doc-comment
/// интерфейса) и делает <c>Users.GetAccounts</c> — самый дешёвый вызов, дающий однозначный ответ
/// "токен принят брокером" без побочных эффектов (plan/13 часть C).
/// </summary>
public sealed class TInvestTokenValidator : ITInvestTokenValidator
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TInvestTokenValidator> _logger;

    public TInvestTokenValidator(IConfiguration configuration, ILogger<TInvestTokenValidator> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TInvestTokenValidationResult> ValidateAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var client = InvestApiClientFactory.Create(new InvestApiSettings
            {
                AccessToken = token,
                AppName = "bonds-portfolio-analytics",
                Sandbox = _configuration.GetValue<bool?>("TInvest:Sandbox") ?? false,
            });

            var response = await client.Users.GetAccountsAsync(new GetAccountsRequest(), cancellationToken: ct);
            var account = response.Accounts.FirstOrDefault(a => a.Status == AccountStatus.Open);
            if (account is null)
            {
                // Токен валиден, но нет ни одного открытого счёта — тоже не то, что нужно синку
                // (BondSyncService.GetPrimaryAccountIdAsync деградировал бы так же).
                return TInvestTokenValidationResult.Invalid(
                    "Токен принят брокером, но у него нет ни одного открытого счёта.");
            }

            return TInvestTokenValidationResult.Valid(account.Id);
        }
        catch (Grpc.Core.RpcException ex)
        {
            // Токен не логируется — только тип/сообщение gRPC-статуса (spec §11).
            _logger.LogWarning(ex, "T-Invest token validation failed: {StatusCode}", ex.StatusCode);
            var reason = ex.StatusCode switch
            {
                Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied =>
                    "Токен не принят T-Invest (недействителен или отозван).",
                _ => $"T-Invest недоступен для проверки токена ({ex.StatusCode}).",
            };
            return TInvestTokenValidationResult.Invalid(reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while validating T-Invest token");
            return TInvestTokenValidationResult.Invalid("Не удалось проверить токен — попробуйте ещё раз позже.");
        }
    }
}
