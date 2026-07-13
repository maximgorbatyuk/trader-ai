# Геймификация рыночного влияния — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Добавить открытый стратегический слой, в котором игрок заключает структурированные соглашения с AI-участниками, публикует легальные или ложные рыночные утверждения и сталкивается с доверием, проверками и регуляторными последствиями.

**Architecture:** Новые механики входят в существующий атомарный рыночный цикл и влияют на цену только через реальные заявки и matching. Соглашения, убеждения, проверки и расследования хранятся как отдельные доменные записи; скрытая модель поведения участника остаётся только на backend и никогда не попадает в игровые API.

**Tech Stack:** .NET 10, C#, EF Core, SQLite, xUnit, React 19, React Router, Vite, Node test runner.

---

## Правила выполнения

- Не добавлять этот файл в staging и не включать его ни в один коммит.
- Перед реализацией прочитать актуальные `AGENTS.md` для backend, services, API, tests, frontend и docs.
- Работать через TDD: сначала падающий тест, затем минимальная реализация, затем focused test и полный релевантный suite.
- Все shell-команды запускать через `rtk`.
- Не менять существующий порядок фаз market cycle без отдельной проверки наблюдаемых ими цен, заявок, денег и состояний компаний.
- Не добавлять прямой `ImpactPercent` стратегическим новостям игрока.
- Не раскрывать `BehaviorModel` ни в одном response DTO, фильтре, сообщении об отказе или элементе UI.
- Не создавать LLM-переговоры, полноценную судебную систему, short selling, взятки, мультиплеер или полную финансовую отчётность.

## Согласованный игровой дизайн

### Игровой цикл

`возможность → предложение или публикация → решение AI → реальные заявки → рыночная реакция → доверие и репутация → подозрение и улики → последствия`

Игра остаётся открытым sandbox без формального условия победы. Игрок самостоятельно выбирает между легальными, серыми и незаконными способами влияния.

### Базовые показатели

- **Trust** хранится отдельно для каждой пары «игрок — контрагент».
- **MarketReputation** является публичной характеристикой игрока.
- **Suspicion** определяет вероятность перехода к расследованию, но само по себе не доказывает нарушение.
- **Evidence** относится к конкретной операции и связывает её с участниками.
- **Influence** не является отдельной валютой; оно выводится из капитала, репутации, доверия и истории соглашений.
- **BehaviorModel** принимает значения `Lawful`, `Opportunistic`, `Predatory` и полностью скрыта от игрока.

### Вертикальный срез первой версии

1. **Public investment thesis** — легальное публичное утверждение, основанное на проверяемых показателях компании.
2. **Price support agreement** — структурированная договорённость с AI-контрагентом о покупках ниже заданного уровня.
3. **Fabricated financial statement** — заведомо ложное утверждение о corporate cash, operating income или dividend capacity.

### Структурированное соглашение

Предложение содержит контрагента, компанию, капитал каждой стороны, уровень поддержки цены, длительность, лимит потерь и фиксированное вознаграждение контрагенту. Первая версия поддерживает фиксированное вознаграждение вместо распределения прибыли: это сохраняет согласованную возможность оплаты партнёра без добавления общего портфеля и отдельного расчёта совместного P&L.

AI отвечает `Accepted`, `Countered`, `Rejected` или `Reported`. Встречное предложение изменяет только один параметр. После отказа действует cooldown на повторное предложение того же типа тому же участнику.

### Информация

Стратегическая публикация проходит цепочку `публикация → распространение → индивидуальная оценка → решение AI → заявка`. Одинаковая новость создаёт разные убеждения у разных участников. Системные кризисы, научные события и ручные операторские новости сохраняют текущее прямое ценовое воздействие.

### Расследование

Аудитор проверяет правдивость утверждений о компании. Регулятор анализирует поведение участников, создаёт evidence и ведёт case через состояния `Monitoring`, `PreliminaryReview`, `Open`, `Cleared`, `Settled`, `Penalized`.

Наказания меняют стратегическое положение, но не завершают sandbox: предупреждение, штраф, изъятие рассчитанной незаконной выгоды, временное ограничение торговли конкретной компанией, запрет новых соглашений, падение репутации и усиленный контроль повторных действий.

## Доменные состояния

### Новые enum

Добавить в `TraderAi/TraderAi/Models/Enums.cs`:

```csharp
public enum BehaviorModel
{
    Lawful,
    Opportunistic,
    Predatory,
}

public enum StrategicAgreementType
{
    PriceSupport,
}

public enum StrategicAgreementStatus
{
    Proposed,
    Countered,
    Active,
    Completed,
    Broken,
    Cancelled,
}

public enum AgreementResponseType
{
    Accepted,
    Countered,
    Rejected,
    Reported,
}

public enum StrategicClaimType
{
    PublicInvestmentThesis,
    FabricatedFinancialStatement,
}

public enum FinancialClaimMetric
{
    CorporateCash,
    RecentOperatingIncome,
    DividendCapacity,
}

public enum ClaimVerificationStatus
{
    Pending,
    Verified,
    Inconclusive,
    Refuted,
}

public enum InvestigationStatus
{
    Monitoring,
    PreliminaryReview,
    Open,
    Cleared,
    Settled,
    Penalized,
}

public enum EvidenceType
{
    ReportedProposal,
    SynchronizedOrders,
    HiddenSponsorship,
    RefutedClaim,
    TradingAroundClaim,
    CounterpartyStatement,
}
```

Добавить `StrategyCampaign`, `StrategyAgreement` и `RegulatoryFine` в `MoneyTransactionType`, а `StrategicClaim`, `ClaimCorrection` и `RegulatoryNotice` — в `NewsCategory`.

### Новые модели

`ParticipantRelationship`:

```csharp
public sealed class ParticipantRelationship
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int CounterpartyId { get; set; }
    public int Trust { get; set; }
    public int CompletedAgreementCount { get; set; }
    public int BrokenAgreementCount { get; set; }
    public int UpdatedInCycleId { get; set; }
}
```

`StrategicAgreement` хранит стороны, target company, status, support price, initial/remaining commitments, fixed fee, eligible-cycle duration, elapsed eligible cycles, cooldown, timestamps и ссылку на исходное соглашение для counteroffer.

`StrategicNewsClaim` хранит `NewsPostId`, игрока, компанию, тип, метрику, заявленное значение, реальное значение на момент публикации, disclosure flag, credibility, reach, стоимость, срок действия и verification status.

`NewsExposure` хранит пару claim/participant, belief от `-1` до `1`, первый и последний цикл воздействия. На пару claim/participant действует unique index.

`SurveillanceProfile` хранит игрока, suspicion, количество подтверждённых нарушений и текущие ограничения. Игровой API возвращает только качественный уровень внимания, а не числовой suspicion.

`InvestigationCase` хранит игрока, status, optional agreement/claim, открывающий и закрывающий цикл, fine и reputation penalty.

`EvidenceItem` хранит case, type, strength, discovered cycle и optional agreement/claim.

`Order` получает nullable `StrategicAgreementId`, чтобы исполнение соглашения можно было проверить по настоящим fills.

## Порядок фаз

Сохранить существующие settlement, margin и maintenance phases. Новые вызовы добавить так:

```text
SettleOpeningCycle
ProcessOpeningDayMargin
MaintainOrdersCore                 # существующая последовательность, Auditor остаётся последним
StrategicNewsService.ProcessForCycle
AgreementService.PrepareOrderIntents
GenerateDecisionsCore
MatchingEngine.Run
AgreementService.ReconcileAfterMatching
Existing dividends and market events
RegulatoryService.ProcessCompletedCycle
Snapshots, behavioural audit, archive, next cycle
```

Все стадии выполняются под `MarketCycleLock` и внутри транзакции `DecideAndAdvanceCoreAsync`.

## Task 1: Добавить persistence foundation

**Files:**

- Modify: `TraderAi/TraderAi/Models/Enums.cs`
- Modify: `TraderAi/TraderAi/Models/Participant.cs`
- Modify: `TraderAi/TraderAi/Models/Order.cs`
- Create: `TraderAi/TraderAi/Models/ParticipantRelationship.cs`
- Create: `TraderAi/TraderAi/Models/StrategicAgreement.cs`
- Create: `TraderAi/TraderAi/Models/StrategicNewsClaim.cs`
- Create: `TraderAi/TraderAi/Models/NewsExposure.cs`
- Create: `TraderAi/TraderAi/Models/SurveillanceProfile.cs`
- Create: `TraderAi/TraderAi/Models/InvestigationCase.cs`
- Create: `TraderAi/TraderAi/Models/EvidenceItem.cs`
- Modify: `TraderAi/TraderAi/Data/AppDbContext.cs`
- Create: `TraderAi/TraderAi.Tests/MarketInfluencePersistenceTests.cs`
- Create: `TraderAi/TraderAi/Migrations/<timestamp>_AddMarketInfluence.cs`
- Create: `TraderAi/TraderAi/Migrations/<timestamp>_AddMarketInfluence.Designer.cs`
- Modify: `TraderAi/TraderAi/Migrations/AppDbContextModelSnapshot.cs`

**Step 1: Write the failing persistence tests**

Добавить SQLite tests, которые проверяют:

```csharp
[Fact]
public async Task RelationshipIsUniqueForPlayerAndCounterparty()
{
    context.ParticipantRelationships.AddRange(
        new ParticipantRelationship { PlayerId = 1, CounterpartyId = 2, UpdatedInCycleId = 1 },
        new ParticipantRelationship { PlayerId = 1, CounterpartyId = 2, UpdatedInCycleId = 1 });

    await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
}

[Fact]
public async Task ExposureIsUniqueForClaimAndParticipant()
{
    context.NewsExposures.AddRange(
        new NewsExposure { StrategicNewsClaimId = 1, ParticipantId = 2, Belief = 0.5m },
        new NewsExposure { StrategicNewsClaimId = 1, ParticipantId = 2, Belief = 0.4m });

    await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
}
```

Также проверить cascade/restrict behavior для agreement orders, claims/news, cases/evidence и default enum conversions.

**Step 2: Run the focused test and verify failure**

Run:

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~MarketInfluencePersistenceTests
```

Expected: FAIL, потому что модели и `DbSet` ещё не существуют.

**Step 3: Add the minimal domain model**

- Добавить `BehaviorModel` и `MarketReputation` в `Participant`.
- Добавить новые models и `DbSet`.
- Настроить unique indexes для relationship, exposure и surveillance profile.
- Настроить indexes по status/cycle для agreements, claims и cases.
- Использовать существующее преобразование enum в string.
- Задать precision `18,6` для credibility, reach, belief, evidence strength и suspicion; денежные значения оставить `18,2`.
- Не добавлять navigation collections, если они не нужны query path первой версии.

**Step 4: Generate the migration**

Run:

```bash
rtk dotnet ef migrations add AddMarketInfluence --project TraderAi/TraderAi/TraderAi.csproj --startup-project TraderAi/TraderAi/TraderAi.csproj
```

Expected: новая migration, designer и обновлённый snapshot.

Проверить, что migration задаёт безопасные defaults существующим participants и не удаляет исторические данные.

**Step 5: Run focused and full backend tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~MarketInfluencePersistenceTests
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 6: Commit production and test files, excluding this plan**

```bash
rtk git add TraderAi/TraderAi/Models/Enums.cs TraderAi/TraderAi/Models/Participant.cs TraderAi/TraderAi/Models/Order.cs TraderAi/TraderAi/Models/ParticipantRelationship.cs TraderAi/TraderAi/Models/StrategicAgreement.cs TraderAi/TraderAi/Models/StrategicNewsClaim.cs TraderAi/TraderAi/Models/NewsExposure.cs TraderAi/TraderAi/Models/SurveillanceProfile.cs TraderAi/TraderAi/Models/InvestigationCase.cs TraderAi/TraderAi/Models/EvidenceItem.cs TraderAi/TraderAi/Data/AppDbContext.cs TraderAi/TraderAi/Migrations/*_AddMarketInfluence.cs TraderAi/TraderAi/Migrations/AppDbContextModelSnapshot.cs TraderAi/TraderAi.Tests/MarketInfluencePersistenceTests.cs
rtk git commit -m "feat: add market influence domain model"
```

## Task 2: Назначить скрытую модель поведения и реализовать переговоры

**Files:**

- Create: `TraderAi/TraderAi/Services/BehaviorModelAssignment.cs`
- Create: `TraderAi/TraderAi/Services/InfluenceOptions.cs`
- Create: `TraderAi/TraderAi/Services/AgreementService.cs`
- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Modify: `TraderAi/TraderAi/Services/MarketExitService.cs`
- Modify: `TraderAi/TraderAi/Program.cs`
- Modify: `TraderAi/TraderAi/appsettings.json`
- Create: `TraderAi/TraderAi.Tests/AgreementServiceTests.cs`
- Modify: `TraderAi/TraderAi.Tests/TestMarketSeed.cs`

**Step 1: Write failing behavior-assignment tests**

Проверить:

- одинаковое стабильное identity всегда получает одинаковый `BehaviorModel`;
- assignment не использует общий `Random` и не меняет scripted draw order;
- seed и replacement получают модель;
- Player не использует скрытую модель для принятия AI-решений.

Использовать стабильный SHA-256 от имени и типа участника, а не `string.GetHashCode`, чтобы результат не менялся между процессами.

**Step 2: Write failing negotiation tests**

Минимальный набор:

```csharp
[Theory]
[InlineData(BehaviorModel.Lawful, AgreementResponseType.Rejected)]
[InlineData(BehaviorModel.Opportunistic, AgreementResponseType.Countered)]
[InlineData(BehaviorModel.Predatory, AgreementResponseType.Accepted)]
public async Task HiddenBehaviorChangesTheSameGreyProposal(
    BehaviorModel behavior,
    AgreementResponseType expected)
{
    var result = await service.ProposePriceSupportAsync(ProposalFor(behavior));

    Assert.Equal(expected, result.Response);
}

[Fact]
public async Task RejectedProposalCreatesCooldown()
{
    var first = await service.ProposePriceSupportAsync(proposal);
    var second = await service.ProposePriceSupportAsync(proposal);

    Assert.Equal(AgreementResponseType.Rejected, first.Response);
    Assert.False(second.Success);
    Assert.Equal("A similar proposal is cooling down.", second.Error);
}
```

Также проверить inactive counterparty, fund member, closed company, invalid amount, insufficient settled/unreserved cash, duplicate active agreement и proposal while restricted.

**Step 3: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AgreementServiceTests
```

Expected: FAIL.

**Step 4: Implement minimal structured negotiation**

`ProposalPriceSupportRequest` должен содержать только:

```csharp
public sealed record PriceSupportProposal(
    int CounterpartyId,
    int CompanyId,
    decimal PlayerCommitment,
    decimal CounterpartyCommitment,
    decimal SupportPrice,
    int EligibleCycleDuration,
    decimal CounterpartyFee);
```

Алгоритм response:

1. Проверить доменные ограничения.
2. Найти или создать relationship с neutral trust `0`.
3. Рассчитать скрытый score из expected capital exposure, fee, trust, temperament, risk profile и behavior model.
4. `Lawful` не принимает заведомо незаконное предложение и может вернуть `Reported`; для grey price support применяет сильный penalty.
5. `Opportunistic` принимает выгодные условия или меняет один параметр.
6. `Predatory` легче принимает, но не получает искусственной гарантии лояльности.
7. Не возвращать score или behavior model вызывающей стороне.

Counteroffer изменяет ровно одно из значений: fee, counterparty commitment, duration или support price. Acceptance/counteroffer должен создать `StrategicAgreement`; rejection создаёт cooldown record в том же agreement row.

**Step 5: Add DI and seed/replacement assignment**

- Зарегистрировать `AgreementService` и `InfluenceOptions` в `Program.cs`.
- Добавить `Influence` section в `appsettings.json` только для deterministic thresholds, durations, costs и penalties.
- Не переносить probability constants из `RandomChanceRatesOptions` в новый options class.
- Назначать `BehaviorModel` при demo seed и replacement без новых random draws.

**Step 6: Run tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter FullyQualifiedName~AgreementServiceTests
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 7: Commit**

```bash
rtk git add TraderAi/TraderAi/Services/BehaviorModelAssignment.cs TraderAi/TraderAi/Services/InfluenceOptions.cs TraderAi/TraderAi/Services/AgreementService.cs TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi/Services/MarketExitService.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi/appsettings.json TraderAi/TraderAi.Tests/AgreementServiceTests.cs TraderAi/TraderAi.Tests/TestMarketSeed.cs
rtk git commit -m "feat: add structured influence negotiations"
```

## Task 3: Исполнять price-support agreement через реальные заявки

**Files:**

- Modify: `TraderAi/TraderAi/Services/AgreementService.cs`
- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Modify: `TraderAi/TraderAi/Services/IDecisionEngine.cs`
- Modify: `TraderAi/TraderAi/Services/RuleBasedDecisionEngine.cs`
- Modify: `TraderAi/TraderAi.Tests/TestDoubles.cs`
- Modify: `TraderAi/TraderAi.Tests/AgreementServiceTests.cs`
- Modify: `TraderAi/TraderAi.Tests/DecisionFlowTests.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketLoopTests.cs`

**Step 1: Write failing commitment tests**

Проверить:

- committed cash уменьшает spendable cash для player и counterparty;
- обычный AI не выставляет противоположную заявку по committed company;
- при цене ниже support level service создаёт buy intent внутри allowed range;
- созданный `Order` содержит `StrategicAgreementId`;
- partial fill уменьшает remaining commitment только на фактически зарезервированную/исполненную сумму согласно выбранному accounting rule;
- выполнение обеих сторон повышает trust;
- сознательная ручная трата или ранний выход помечает agreement как `Broken`;
- margin call и loan distress имеют приоритет и не считаются предательством;
- LULD pause не увеличивает eligible elapsed cycles;
- закрытие компании или выход counterparty завершает agreement как `Cancelled` без trust penalty.

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AgreementServiceTests|FullyQualifiedName~DecisionFlowTests"
```

Expected: FAIL.

**Step 3: Implement commitment-aware available cash**

Не добавлять второй денежный баланс. `AgreementService` возвращает outstanding commitment per participant; `MarketService` вычитает его:

```csharp
var discretionaryCash = Math.Max(
    0m,
    participant.AvailableBalance - committedCashByParticipant.GetValueOrDefault(participant.Id));
```

Ту же проверку применять к manual player order, чтобы обещанный капитал нельзя было потратить дважды. Когда agreement intent становится обычным order, соответствующая часть commitment переходит в существующий `ReservedBalance` и `ReservedCashAmount`.

**Step 4: Generate and place agreement intents**

`AgreementService.PrepareOrderIntentsAsync` возвращает records, но не создаёт orders самостоятельно:

```csharp
public sealed record AgreementOrderIntent(
    int AgreementId,
    int ParticipantId,
    int CompanyId,
    int Quantity,
    decimal LimitPrice);
```

`MarketService` передаёт intent через существующую order validation с optional `strategicAgreementId`. Это сохраняет LULD bounds, opposite-side rule, margin checks и общий accounting path.

**Step 5: Integrate cycle phases**

В `DecideAndAdvanceCoreAsync`, после `MaintainOrdersCoreAsync` и до `GenerateDecisionsCoreAsync`:

1. обновить news beliefs, когда сервис появится;
2. получить agreement intents;
3. разместить их batch-ом;
4. исключить committed company из обычного decision pass.

Сразу после `matchingEngine.RunAsync` вызвать reconciliation до расчёта agreement result. Не добавлять отдельный `SaveChangesAsync` внутри participant loop.

**Step 6: Prove atomic rollback**

Добавить integration test, где service после matching выбрасывает exception. Assert: новый order, fill, commitment decrement и trust change отсутствуют после rollback.

**Step 7: Run focused and full backend suites**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~AgreementServiceTests|FullyQualifiedName~DecisionFlowTests|FullyQualifiedName~MarketLoopTests"
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 8: Commit**

```bash
rtk git add TraderAi/TraderAi/Services/AgreementService.cs TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi/Services/IDecisionEngine.cs TraderAi/TraderAi/Services/RuleBasedDecisionEngine.cs TraderAi/TraderAi/Models/Order.cs TraderAi/TraderAi.Tests/TestDoubles.cs TraderAi/TraderAi.Tests/AgreementServiceTests.cs TraderAi/TraderAi.Tests/DecisionFlowTests.cs TraderAi/TraderAi.Tests/MarketLoopTests.cs
rtk git commit -m "feat: execute influence agreements through the order book"
```

## Task 4: Добавить strategic claims, exposure и belief-driven demand

**Files:**

- Create: `TraderAi/TraderAi/Services/StrategicNewsService.cs`
- Create: `TraderAi/TraderAi/Services/DemoStrategicNewsContent.cs`
- Modify: `TraderAi/TraderAi/Services/IDecisionEngine.cs`
- Modify: `TraderAi/TraderAi/Services/RuleBasedDecisionEngine.cs`
- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Modify: `TraderAi/TraderAi/Program.cs`
- Modify: `TraderAi/TraderAi/appsettings.json`
- Create: `TraderAi/TraderAi.Tests/StrategicNewsServiceTests.cs`
- Modify: `TraderAi/TraderAi.Tests/RuleBasedDecisionEngineTests.cs`
- Modify: `TraderAi/TraderAi.Tests/TestDoubles.cs`
- Modify: `TraderAi/TraderAi.Tests/NewsServiceTests.cs`

**Step 1: Write failing publication tests**

Проверить:

- public thesis создаёт `NewsPost` и `StrategicNewsClaim` атомарно;
- fabricated statement разрешает только проверяемые metrics;
- campaign cost списывается с settled, unreserved player cash как `StrategyCampaign`;
- недостаток денег не создаёт ни post, ни claim, ни money transaction;
- strategic post имеет `Scope.None`, `ImpactPercent == null`, `ImpactAppliedInCycleId == null`;
- publication itself не создаёт `PriceSnapshot`;
- hidden sponsorship и false claim сохраняются backend-side, но не выводятся обычным news response до расследования.

**Step 2: Write failing exposure and belief tests**

Проверить:

- reach deterministically выбирает подмножество active AI participants;
- одна claim не создаёт duplicate exposure;
- conservative/low-risk participant требует более высокой credibility;
- predatory participant может торговать на сомнительной информации, не обязательно веря ей;
- belief затухает и перестаёт влиять после expiry;
- reputation источника и corroborating claims меняют belief;
- player и player-managed fund не получают automated decisions.

**Step 3: Write failing decision-engine tests**

Расширить `DecisionContext`:

```csharp
public sealed record DecisionContext(
    Participant Participant,
    decimal AvailableCash,
    IReadOnlyList<CompanyQuote> Companies,
    IReadOnlyDictionary<int, int> SharesOwnedByCompany,
    IReadOnlySet<int> CompaniesWithOpenOrders,
    bool CrisisActive = false,
    decimal LoanLiability = 0m,
    IReadOnlyDictionary<int, decimal>? NewsSignalByCompany = null);
```

Tests должны доказать, что positive signal увеличивает buy frequency, refutation/negative signal увеличивает sell pressure у holders, а zero signal сохраняет текущую seeded sequence.

**Step 4: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategicNewsServiceTests|FullyQualifiedName~RuleBasedDecisionEngineTests"
```

Expected: FAIL.

**Step 5: Implement strategic publication and exposure**

- Получать player server-side; request не содержит player id.
- Сохранять фактическое значение metric на момент публикации.
- Для reach использовать stable hash от claim id и participant id, чтобы не менять общий random draw order.
- Рассчитывать belief из credibility, player reputation, participant temperament/risk и hidden behavior.
- Ограничить signal диапазоном `[-1, 1]`.
- В `GenerateDecisionsCoreAsync` загрузить exposures batch-ом и передать per-participant map в pure decision engine.
- Добавить signal weight к existing buy/sell pulls и сохранить существующие caps.

**Step 6: Integrate before decisions**

Вызвать `StrategicNewsService.ProcessForCycleAsync` после maintenance/auditor phase и до agreement intents. Verification/correction текущего цикла тогда попадут в beliefs до обычных решений.

**Step 7: Run focused and full tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategicNewsServiceTests|FullyQualifiedName~RuleBasedDecisionEngineTests|FullyQualifiedName~NewsServiceTests"
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS, включая прежние direct-impact news tests.

**Step 8: Commit**

```bash
rtk git add TraderAi/TraderAi/Services/StrategicNewsService.cs TraderAi/TraderAi/Services/DemoStrategicNewsContent.cs TraderAi/TraderAi/Services/IDecisionEngine.cs TraderAi/TraderAi/Services/RuleBasedDecisionEngine.cs TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi/appsettings.json TraderAi/TraderAi.Tests/StrategicNewsServiceTests.cs TraderAi/TraderAi.Tests/RuleBasedDecisionEngineTests.cs TraderAi/TraderAi.Tests/TestDoubles.cs TraderAi/TraderAi.Tests/NewsServiceTests.cs
rtk git commit -m "feat: drive market demand from strategic claims"
```

## Task 5: Проверять claims через существующих аудиторов

**Files:**

- Modify: `TraderAi/TraderAi/Services/AuditorService.cs`
- Modify: `TraderAi/TraderAi/Services/StrategicNewsService.cs`
- Create: `TraderAi/TraderAi.Tests/StrategicClaimAuditTests.cs`
- Modify: `TraderAi/TraderAi.Tests/AuditorServiceTests.cs`

**Step 1: Write failing verification tests**

Проверить три исхода:

```csharp
[Theory]
[InlineData(FinancialClaimMetric.CorporateCash, ClaimVerificationStatus.Verified)]
[InlineData(FinancialClaimMetric.RecentOperatingIncome, ClaimVerificationStatus.Refuted)]
[InlineData(FinancialClaimMetric.DividendCapacity, ClaimVerificationStatus.Inconclusive)]
public async Task AuditorStoresAResultForAMatureClaim(
    FinancialClaimMetric metric,
    ClaimVerificationStatus expected)
{
    await SeedClaimAsync(metric);

    await auditor.ProcessForCycleAsync(cycle.Id, cycle.CycleNumber, now);

    Assert.Equal(expected, (await context.StrategicNewsClaims.SingleAsync()).VerificationStatus);
}
```

Дополнительно проверить: слишком свежая claim не проверяется; auditor review другой компании не затрагивает claim; refutation создаёт correction news без direct price impact; existing auditor draw order остаётся документированным и протестированным.

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategicClaimAuditTests|FullyQualifiedName~AuditorServiceTests"
```

Expected: FAIL.

**Step 3: Implement verification without a second auditor system**

Когда существующий auditor уже выбрал компанию, проверить mature active claims этой компании:

- `CorporateCash` сравнивается с `Company.CashBalance`.
- `RecentOperatingIncome` сравнивается с суммой recent `CorporateCashTransaction` типа `OperatingIncome`.
- `DividendCapacity` сравнивается с corporate cash и последними dividend outcomes; при недостаточном окне вернуть `Inconclusive`.

Refuted claim:

- получает `VerificationStatus.Refuted` и verification cycle;
- создаёт `NewsPost` категории `ClaimCorrection`, `Scope.None`, без `ImpactPercent`;
- создаёт negative belief signal через `StrategicNewsService`;
- не штрафует игрока напрямую — это делает regulator.

**Step 4: Run focused and full tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategicClaimAuditTests|FullyQualifiedName~AuditorServiceTests"
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 5: Commit**

```bash
rtk git add TraderAi/TraderAi/Services/AuditorService.cs TraderAi/TraderAi/Services/StrategicNewsService.cs TraderAi/TraderAi.Tests
rtk git commit -m "feat: verify strategic claims through company audits"
```

## Task 6: Добавить surveillance, evidence, cases и последствия

**Files:**

- Create: `TraderAi/TraderAi/Services/RegulatoryService.cs`
- Modify: `TraderAi/TraderAi/Services/AgreementService.cs`
- Modify: `TraderAi/TraderAi/Services/MarketService.cs`
- Modify: `TraderAi/TraderAi/Program.cs`
- Modify: `TraderAi/TraderAi/appsettings.json`
- Create: `TraderAi/TraderAi.Tests/RegulatoryServiceTests.cs`
- Modify: `TraderAi/TraderAi.Tests/MarketLoopTests.cs`
- Modify: `TraderAi/TraderAi.Tests/AccountingReconciliationTests.cs`

**Step 1: Write failing suspicion/evidence tests**

Проверить:

- synchronized orders повышают suspicion, но не создают automatic penalty;
- refuted fabricated claim создаёт `RefutedClaim` evidence;
- player sell fills после fabricated claim создают `TradingAroundClaim` evidence;
- public disclosed thesis не создаёт hidden-sponsorship evidence;
- reported proposal создаёт case link к agreement;
- suspicion постепенно снижается, когда нет новых signals;
- evidence history не удаляется при decay suspicion.

**Step 2: Write failing case-state tests**

Проверить переходы:

```text
Monitoring -> PreliminaryReview -> Open -> Cleared
Monitoring -> PreliminaryReview -> Open -> Settled
Monitoring -> PreliminaryReview -> Open -> Penalized
```

Punishment разрешён только при достаточном суммарном evidence strength. Suspicion без evidence заканчивается `Cleared`.

**Step 3: Write failing consequence tests**

Проверить:

- fine не превышает spendable player cash и создаёт `RegulatoryFine` transaction;
- restriction блокирует manual order только по указанной company;
- agreement restriction блокирует новые proposals до expiry;
- penalty снижает reputation и trust затронутых relationships;
- repeated violation усиливает penalty;
- permanent ban не создаётся;
- market reset удаляет profiles, cases и evidence в правильном dependency order.

**Step 4: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~RegulatoryServiceTests|FullyQualifiedName~AccountingReconciliationTests"
```

Expected: FAIL.

**Step 5: Implement post-match surveillance**

`RegulatoryService.ProcessCompletedCycleAsync` работает после matching и системных событий, но до snapshots/archive. Сервис batch-ом читает current-cycle orders/fills, newly refuted claims и reports, затем:

1. добавляет suspicion signals;
2. создаёт evidence только при конкретной связи;
3. продвигает case state;
4. применяет outcome;
5. публикует `RegulatoryNotice` без direct price impact.

Counterparty testimony зависит от trust и hidden behavior, но exact behavior не записывается в public case response.

**Step 6: Preserve atomicity and accounting**

- Fine и reputation changes сохраняются в общей cycle transaction.
- Искусственно вызвать exception после regulator phase и доказать rollback всего tick.
- Добавить regulator tables в reset order до удаления participants/news/agreements.

**Step 7: Run focused and full tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~RegulatoryServiceTests|FullyQualifiedName~MarketLoopTests|FullyQualifiedName~AccountingReconciliationTests"
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 8: Commit**

```bash
rtk git add TraderAi/TraderAi/Services/RegulatoryService.cs TraderAi/TraderAi/Services/AgreementService.cs TraderAi/TraderAi/Services/MarketService.cs TraderAi/TraderAi/Program.cs TraderAi/TraderAi/appsettings.json TraderAi/TraderAi.Tests/RegulatoryServiceTests.cs TraderAi/TraderAi.Tests/MarketLoopTests.cs TraderAi/TraderAi.Tests/AccountingReconciliationTests.cs
rtk git commit -m "feat: add market manipulation investigations"
```

## Task 7: Добавить strategy API без утечки скрытой модели

**Files:**

- Modify: `TraderAi/TraderAi/Api/MarketEndpoints.cs`
- Create: `TraderAi/TraderAi.Tests/StrategyApiTests.cs`
- Modify: `TraderAi/TraderAi.Tests/ApiTests.cs`

**Step 1: Write failing API contract tests**

Добавить tests для:

- `GET /strategy`;
- `GET /strategy/counterparties`;
- `POST /strategy/agreements`;
- `POST /strategy/agreements/{id}/accept-counteroffer`;
- `POST /strategy/claims`.

Проверить status codes: no player, missing company, inactive counterparty, invalid amount, active restriction, accepted, countered, rejected, reported.

Отдельный security-by-contract test:

```csharp
[Fact]
public async Task GameplayResponsesNeverExposeBehaviorModel()
{
    using var participant = await client.GetAsync($"/participants/{counterpartyId}");
    using var counterparties = await client.GetAsync("/strategy/counterparties");
    using var proposal = await client.PostAsJsonAsync("/strategy/agreements", request);

    Assert.DoesNotContain("behaviorModel", await participant.Content.ReadAsStringAsync());
    Assert.DoesNotContain("behaviorModel", await counterparties.Content.ReadAsStringAsync());
    Assert.DoesNotContain("behaviorModel", await proposal.Content.ReadAsStringAsync());
}
```

**Step 2: Run focused tests and verify failure**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategyApiTests|FullyQualifiedName~ApiTests"
```

Expected: FAIL or 404.

**Step 3: Add minimal request/response contracts**

`GET /strategy` возвращает:

- player reputation;
- qualitative regulatory attention: `Low`, `Elevated`, `High`;
- active restrictions;
- active/recent agreements;
- active/recent claims;
- open/recent cases.

`GET /strategy/counterparties` возвращает только observable data: id, name, temperament, risk profile, available public worth data, qualitative trust, cooldown and eligibility. Не возвращать behavior model или hidden acceptance score.

`POST` endpoints server-side определяют текущего Player; request не может инициировать действие от имени другого participant или managed fund.

**Step 4: Keep endpoint logic thin**

Endpoints валидируют transport shape и делегируют orchestration в `MarketService`/domain services под общей cycle lock. Не дублировать scoring, accounting или state transitions в `MarketEndpoints.cs`.

**Step 5: Run focused and full backend tests**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj --filter "FullyQualifiedName~StrategyApiTests|FullyQualifiedName~ApiTests"
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS.

**Step 6: Commit**

```bash
rtk git add TraderAi/TraderAi/Api/MarketEndpoints.cs TraderAi/TraderAi.Tests/StrategyApiTests.cs TraderAi/TraderAi.Tests/ApiTests.cs
rtk git commit -m "feat: expose market influence strategy endpoints"
```

## Task 8: Добавить Strategy page

**Files:**

- Create: `frontend/src/StrategyPage.jsx`
- Create: `frontend/src/strategyModel.js`
- Create: `frontend/src/strategyModel.test.js`
- Modify: `frontend/src/api.js`
- Modify: `frontend/src/main.jsx`
- Modify: `frontend/src/AppShell.jsx`
- Modify: `frontend/src/App.css`

**Step 1: Write failing pure-model tests**

Проверить:

- request builder не отправляет player id;
- commitment/fee/support price должны быть положительными;
- duration clamp совпадает с API contract;
- regulatory attention и trust получают текстовый label и не зависят только от цвета;
- counteroffer diff показывает ровно один изменённый параметр;
- behavior model не ожидается frontend model.

Run:

```bash
rtk node --test frontend/src/strategyModel.test.js
```

Expected: FAIL, потому что module ещё не существует.

**Step 2: Implement API methods**

Добавить в `frontend/src/api.js`:

```javascript
getStrategy: () => get('/strategy'),
getStrategyCounterparties: () => get('/strategy/counterparties'),
proposePriceSupport: (payload) => post('/strategy/agreements', payload),
acceptStrategyCounteroffer: (agreementId) => post(`/strategy/agreements/${agreementId}/accept-counteroffer`),
publishStrategyClaim: (payload) => post('/strategy/claims', payload),
```

**Step 3: Build one focused page without premature component abstraction**

`StrategyPage.jsx` должен:

- рендерить только `<main className="main">`;
- выполнять immediate load + polling с cleanup, совместимый со StrictMode;
- использовать существующие `Panel`, `CompanyCombobox`, buttons, inputs и design tokens;
- показывать reputation, qualitative regulatory attention и restrictions;
- содержать structured price-support form;
- содержать public thesis/fabricated statement form;
- показывать active agreements, relationship trust, claims и investigations;
- показывать loading, empty, pending и honest error states;
- не показывать exact suspicion, evidence not yet disclosed или behavior model;
- не полагаться только на green/red для статусов.

Сначала оставить две формы внутри страницы. Выносить отдельные components только если файл действительно становится трудно читать после рабочей реализации.

**Step 4: Add route and navigation**

- Добавить `/strategy` под pathless `AppShell` layout в `frontend/src/main.jsx`.
- Добавить `Strategy` рядом с `Player stats` в `AppShell`.
- Не добавлять новый market polling loop в shell; page polls только strategy-specific endpoints.

**Step 5: Add minimal responsive styles**

Использовать существующую плотность, focus states и CSS variables. Добавить grid, form rows, state badges и compact timeline без новых цветов вне design tokens. Уважать existing reduced-motion behavior.

**Step 6: Run frontend tests, lint and build**

```bash
rtk node --test frontend/src/strategyModel.test.js
rtk node --test frontend/src/*.test.js
rtk npm --prefix frontend run lint
rtk npm --prefix frontend run build
```

Expected: PASS.

**Step 7: Commit**

```bash
rtk git add frontend/src/StrategyPage.jsx frontend/src/strategyModel.js frontend/src/strategyModel.test.js frontend/src/api.js frontend/src/main.jsx frontend/src/AppShell.jsx frontend/src/App.css
rtk git commit -m "feat: add market influence strategy page"
```

## Task 9: Обновить durable documentation

**Files:**

- Create: `docs/logic/market-influence.md`
- Modify: `docs/architecture.md`
- Modify: `docs/domain.md`
- Modify: `docs/roles/player.md`
- Modify: `README.md`

**Step 1: Write focused product documentation**

`docs/logic/market-influence.md` описывает:

- legal/grey/illegal actions;
- structured agreements;
- trust, reputation, suspicion and evidence;
- hidden behavior model как неизвестную игроку характеристику без перечисления внутреннего API;
- strategic news lifecycle;
- audit/regulatory distinction;
- edge cases LULD, closure, exit, margin and loans.

Не копировать implementation identifiers и не ссылаться на этот план.

**Step 2: Update canonical pages**

- `docs/architecture.md`: новая subsystem boundary и cycle ordering.
- `docs/domain.md`: новые durable entities и core rules.
- `docs/roles/player.md`: новые доступные действия и ограничения.
- `README.md`: краткое описание игрового слоя и новая страница в documentation table.

**Step 3: Verify links and formatting**

```bash
rtk rg -n "market-influence|Market influence" README.md docs
rtk git diff --check
```

Expected: ссылки разрешаются, whitespace errors отсутствуют.

**Step 4: Commit documentation, excluding this plan**

```bash
rtk git add README.md docs/architecture.md docs/domain.md docs/roles/player.md docs/logic/market-influence.md
rtk git commit -m "docs: describe market influence gameplay"
```

## Task 10: Полная проверка и ручной сценарий

**Files:**

- Verify only; change production files только при обнаружении дефекта.

**Step 1: Run the complete backend suite**

```bash
rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj
```

Expected: PASS, zero failed.

**Step 2: Run all frontend verification**

```bash
rtk node --test frontend/src/*.test.js
rtk npm --prefix frontend run lint
rtk npm --prefix frontend run build
```

Expected: PASS.

**Step 3: Run the application**

```bash
rtk ./start-dev.sh
```

Manual acceptance flow:

1. Seed market and create Player.
2. Open Strategy page and confirm no hidden behavior label exists.
3. Submit price-support proposal; observe accepted/countered/rejected/reported outcome.
4. For an active agreement, step cycles until real agreement-linked orders appear in the ordinary order book.
5. Publish public thesis and confirm no immediate price snapshot appears solely from publication.
6. Step cycles and observe different AI demand reactions.
7. Publish fabricated statement, wait for auditor review, observe correction and investigation state.
8. Confirm restriction blocks only its documented action and later expires.
9. Confirm keyboard focus, error states and non-color status labels.

**Step 4: Check the final diff and plan-file rule**

```bash
rtk git diff --check
rtk git status --short
```

Expected:

- no whitespace errors;
- no unrelated user changes included;
- `docs/plans/gamificaion.md` remains unstaged and uncommitted.

## Готовность первой версии

Вертикальный срез готов, когда:

- AI принимает структурированные предложения с учётом скрытой behavior model;
- игрок не может увидеть behavior model через UI или API;
- price support исполняется только обычными orders и fills;
- strategic claims влияют на individual beliefs и decisions, а не напрямую на price;
- auditor может подтвердить, не подтвердить или опровергнуть claim;
- regulator отличает suspicion от evidence и не штрафует без доказательств;
- последствия ограничены по времени и не завершают sandbox;
- LULD, company closure, participant exit, margin и loans не создают ложное предательство;
- reset очищает новые данные в корректном порядке;
- полный backend suite, frontend tests, lint и build проходят.
