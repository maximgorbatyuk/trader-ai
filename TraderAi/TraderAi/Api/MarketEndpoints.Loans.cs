using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapLoanEndpoints(this WebApplication app)
    {
        app.MapGet("/bankruptcies", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var bankruptcies = await dbContext.Bankruptcies
                .OrderByDescending(bankruptcy => bankruptcy.Id)
                .Take(limit)
                .ToListAsync();

            var participantIds = bankruptcies.Select(bankruptcy => bankruptcy.ParticipantId).Distinct().ToList();
            var participantNameById = await dbContext.Participants
                .Where(participant => participantIds.Contains(participant.Id))
                .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);

            // A bankrupt trader may since have left the market for good: its Participant row is deleted, so fall
            // back to the name archived on its MarketExit row before the numeric-id last resort in the mapper.
            var departedIds = participantIds.Where(id => !participantNameById.ContainsKey(id)).ToList();
            if (departedIds.Count > 0)
            {
                var departedNameById = await dbContext.MarketExits
                    .Where(marketExit => departedIds.Contains(marketExit.ParticipantId))
                    .ToDictionaryAsync(marketExit => marketExit.ParticipantId, marketExit => marketExit.Name);
                foreach (var departed in departedNameById)
                {
                    participantNameById[departed.Key] = departed.Value;
                }
            }

            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            var response = bankruptcies
                .Select(bankruptcy => ToBankruptcyResponse(bankruptcy, participantNameById, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/market-exits", async (int? take, AppDbContext dbContext) =>
        {
            var limit = Math.Clamp(take ?? 30, 1, 200);
            var marketExits = await dbContext.MarketExits
                .OrderByDescending(marketExit => marketExit.Id)
                .Take(limit)
                .ToListAsync();

            var cycleNumberById = await dbContext.MarketCycles
                .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

            // The trader's name is denormalised onto the exit row (its Participant row is gone), so no join.
            var response = marketExits
                .Select(marketExit => ToMarketExitResponse(marketExit, cycleNumberById))
                .ToArray();

            return Results.Ok(response);
        });

        app.MapGet("/banks", async (AppDbContext dbContext) =>
        {
            var banks = await dbContext.Banks.OrderBy(bank => bank.Id).ToListAsync();
            var openByBank = (await dbContext.Loans
                    .Where(loan => loan.Status == LoanStatus.Open)
                    .Select(loan => new { loan.BankId, loan.RemainingPrincipal })
                    .ToListAsync())
                .GroupBy(loan => loan.BankId)
                .ToDictionary(
                    group => group.Key,
                    group => new { Count = group.Count(), Outstanding = group.Sum(loan => loan.RemainingPrincipal) });

            var items = banks
                .Select(bank => new BankResponse(
                    bank.Id,
                    bank.Name,
                    bank.InterestRate,
                    bank.Balance,
                    openByBank.TryGetValue(bank.Id, out var open) ? open.Count : 0,
                    openByBank.TryGetValue(bank.Id, out var open2) ? open2.Outstanding : 0m))
                .ToArray();

            return Results.Ok(items);
        });

        app.MapGet("/loans/paged", async (
            int? page, int? pageSize, int? bankId, string? status, string? sort, string? sortDir,
            AppDbContext dbContext) =>
        {
            var (pageIndex, size) = ResolvePaging(page, pageSize, 20);
            var descending = SortDescending(sortDir);

            var query = dbContext.Loans.AsQueryable();
            if (bankId is int bank)
            {
                query = query.Where(loan => loan.BankId == bank);
            }

            query = FilterLoansByStatus(query, status);
            var total = await query.CountAsync();

            IOrderedQueryable<Loan> ordered = sort switch
            {
                "principal" => descending ? query.OrderByDescending(loan => loan.RemainingPrincipal) : query.OrderBy(loan => loan.RemainingPrincipal),
                "pastDue" => descending
                    ? query.OrderByDescending(loan => loan.PastDuePrincipal + loan.PastDueInterest + loan.AccruedFees)
                    : query.OrderBy(loan => loan.PastDuePrincipal + loan.PastDueInterest + loan.AccruedFees),
                "term" => descending ? query.OrderByDescending(loan => loan.TermTradingDays) : query.OrderBy(loan => loan.TermTradingDays),
                _ => descending ? query.OrderByDescending(loan => loan.Id) : query.OrderBy(loan => loan.Id),
            };

            var loans = await ordered.Skip((pageIndex - 1) * size).Take(size).ToListAsync();
            var items = (await BuildLoanResponsesAsync(dbContext, loans)).ToArray();
            return Results.Ok(new PagedLoansResponse(items, total, pageIndex, size));
        });

        app.MapPost("/loans/{loanId:int}/repay", async (int loanId, RepayLoanRequest? request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.RepayLoanAsync(loanId, request?.Amount);
            if (!result.Success || result.Loan is null)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            var response = (await BuildLoanResponsesAsync(dbContext, [result.Loan])).Single();
            return Results.Ok(response);
        });

        // Player-initiated borrowing for a participant (the player or its managed fund). The amount is capped to
        // a fraction of gross worth inside the service, so an over-cap request comes back as a 400.
        app.MapPost("/participants/{participantId:int}/loans", async (int participantId, BorrowLoanRequest? request, MarketService marketService, AppDbContext dbContext) =>
        {
            var result = await marketService.BorrowLoanAsync(participantId, request?.Amount ?? 0m);
            if (!result.Success || result.Loan is null)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            var response = (await BuildLoanResponsesAsync(dbContext, [result.Loan])).Single();
            return Results.Ok(response);
        });
    }
}
