# ReleaseFin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Jellyfin 10.10 plugin that drip-releases episodes of selected series to selected accounts on cron schedules, hiding unreleased episodes via `releasefin-*` tags in the users' blocked-tags parental control.

**Architecture:** Pure decision logic (cron math, episode ordering, tag list math) lives in `Core/` and is unit-tested. A thin `ReleaseManager` applies decisions through `ILibraryManager`/`IUserManager`. One `IHostedService` runs a 1-minute timer (catch-up is inherent: it counts cron occurrences since the persisted `LastRunUtc`) and subscribes to `ItemAdded` so new episodes of scheduled series arrive locked. A REST controller + embedded dashboard page provide admin UI.

**Tech Stack:** C# / net8.0, `Jellyfin.Controller` + `Jellyfin.Model` 10.10.7 (`ExcludeAssets=runtime`), Cronos 0.13.0, xunit. Spec: `docs/superpowers/specs/2026-07-06-releasefin-design.md`.

**Verified API facts (Jellyfin 10.10.7 — do not re-derive):**
- Service registration: `MediaBrowser.Controller.Plugins.IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)`; hosted services and singletons both work.
- Episodes of a series: `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], AncestorIds = [seriesId], Recursive = true })` (`BaseItemKind` from `Jellyfin.Data.Enums`; use `AncestorIds`, not `ParentId`).
- Persist tag change: `item.Tags` is `string[]`; `await libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct)` (`ItemUpdateType` in `MediaBrowser.Controller.Library`).
- Blocked tags (preferred over legacy `UpdatePolicyAsync`): `user.GetPreference(PreferenceKind.BlockedTags)` / `user.SetPreference(PreferenceKind.BlockedTags, string[])` then `await userManager.UpdateUserAsync(user)`. `User` = `Jellyfin.Data.Entities.User`, `PreferenceKind` from `Jellyfin.Data.Enums`.
- Events: `ILibraryManager.ItemAdded` is `EventHandler<ItemChangeEventArgs>` (`MediaBrowser.Controller.Library`; args: `Item`, `Parent`, `UpdateReason`).
- `Episode` = `MediaBrowser.Controller.Entities.TV.Episode`; `IndexNumber`/`ParentIndexNumber` are `int?`, `SeriesId` is `Guid`.
- Controllers in the plugin assembly are auto-discovered. Auth: `[Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]`. Needs `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- Cronos: `CronExpression.Parse(expr)` (5-field), `GetOccurrences(fromUtc, toUtc, TimeZoneInfo.Local, fromInclusive, toInclusive)`; instants must be `DateTimeKind.Utc`. `GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local)` returns `DateTime?`.
- Deployment note: Cronos.dll must be copied into the plugin folder alongside the plugin DLL.

**File structure:**

```
Jellyfin.Plugin.ReleaseFin.sln
src/Jellyfin.Plugin.ReleaseFin/
  Jellyfin.Plugin.ReleaseFin.csproj
  Plugin.cs                          # BasePlugin + config page registration
  PluginServiceRegistrator.cs        # DI: ReleaseManager singleton + hosted service
  Configuration/PluginConfiguration.cs
  Configuration/ReleaseSchedule.cs   # persisted schedule model
  Configuration/configPage.html      # embedded admin UI
  Core/ReleaseFinTag.cs              # tag naming + idempotent string[] math (pure)
  Core/EpisodeKey.cs                 # aired-order key, specials, offset compare (pure)
  Core/ScheduleCalculator.cs         # cron validate / due-tick count / next occurrence (pure)
  ReleaseManager.cs                  # thin Jellyfin glue: apply/release/remove/lock-new
  ReleaseFinEntrypoint.cs            # IHostedService: timer + ItemAdded subscription
  Api/ReleaseFinController.cs        # REST for the config page
tests/Jellyfin.Plugin.ReleaseFin.Tests/
  Jellyfin.Plugin.ReleaseFin.Tests.csproj
  ReleaseFinTagTests.cs
  EpisodeKeyTests.cs
  ScheduleCalculatorTests.cs
.github/workflows/build.yml
dev/docker-compose.yml               # manual integration testing
```

**Testing boundary (intentional):** `ReleaseManager`, `ReleaseFinEntrypoint`, and the controller are thin glue over verified Jellyfin APIs; constructing Jellyfin entities (`Episode`, `User`) in unit tests fights BaseItem static dependencies for little value (official plugin repos don't do it either). All branching logic they rely on lives in `Core/` and IS unit-tested. Glue is verified by the Task 10 integration checklist.

---

### Task 1: Solution scaffold

**Files:**
- Create: `Jellyfin.Plugin.ReleaseFin.sln`
- Create: `src/Jellyfin.Plugin.ReleaseFin/Jellyfin.Plugin.ReleaseFin.csproj`
- Create: `src/Jellyfin.Plugin.ReleaseFin/Plugin.cs`
- Create: `src/Jellyfin.Plugin.ReleaseFin/Configuration/PluginConfiguration.cs`
- Create: `src/Jellyfin.Plugin.ReleaseFin/Configuration/configPage.html` (placeholder; real UI in Task 9)
- Create: `tests/Jellyfin.Plugin.ReleaseFin.Tests/Jellyfin.Plugin.ReleaseFin.Tests.csproj`

- [ ] **Step 1: Create the plugin csproj**

`src/Jellyfin.Plugin.ReleaseFin/Jellyfin.Plugin.ReleaseFin.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Jellyfin.Plugin.ReleaseFin</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.10.7" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="10.10.7" ExcludeAssets="runtime" />
    <PackageReference Include="Cronos" Version="0.13.0" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Configuration\configPage.html" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create PluginConfiguration and Plugin**

`src/Jellyfin.Plugin.ReleaseFin/Configuration/PluginConfiguration.cs`:

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseFin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
}
```

`src/Jellyfin.Plugin.ReleaseFin/Plugin.cs`:

```csharp
using System.Globalization;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ReleaseFin;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "ReleaseFin";

    public override string Description =>
        "Drip-release episodes to selected accounts on a schedule, like weekly TV.";

    public override Guid Id => Guid.Parse("e7d1f0a4-8c3b-4a5e-9f2d-6b0c4d8e1a23");

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        }
    ];
}
```

`src/Jellyfin.Plugin.ReleaseFin/Configuration/configPage.html` (placeholder for now):

```html
<!DOCTYPE html>
<html>
<head><title>ReleaseFin</title></head>
<body>
<div id="ReleaseFinConfigPage" data-role="page" class="page type-interior pluginConfigurationPage">
    <div data-role="content"><div class="content-primary"><h1>ReleaseFin</h1></div></div>
</div>
</body>
</html>
```

- [ ] **Step 3: Create the test project**

`tests/Jellyfin.Plugin.ReleaseFin.Tests/Jellyfin.Plugin.ReleaseFin.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Jellyfin.Plugin.ReleaseFin\Jellyfin.Plugin.ReleaseFin.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create solution and add projects**

```bash
cd /home/detair/GIT/detair/releasefin
dotnet new sln -n Jellyfin.Plugin.ReleaseFin
dotnet sln add src/Jellyfin.Plugin.ReleaseFin tests/Jellyfin.Plugin.ReleaseFin.Tests
```

- [ ] **Step 5: Build**

Run: `dotnet build --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: scaffold Jellyfin plugin solution"
```

---

### Task 2: ReleaseSchedule model

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/Configuration/ReleaseSchedule.cs`
- Modify: `src/Jellyfin.Plugin.ReleaseFin/Configuration/PluginConfiguration.cs`

- [ ] **Step 1: Create the model** (XML-serializable: public setters, parameterless ctor, arrays not lists for Guid collections is not required — `List<>` serializes fine, but arrays keep it simple)

`src/Jellyfin.Plugin.ReleaseFin/Configuration/ReleaseSchedule.cs`:

```csharp
namespace Jellyfin.Plugin.ReleaseFin.Configuration;

/// <summary>One drip-release assignment: a series, the restricted users, and the cadence.</summary>
public class ReleaseSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid SeriesId { get; set; }

    public Guid[] UserIds { get; set; } = [];

    /// <summary>5-field cron, evaluated in the server's local time zone.</summary>
    public string CronExpression { get; set; } = "0 16 * * *";

    public int EpisodesPerTick { get; set; } = 1;

    /// <summary>Episodes at or before S(InitialSeason)E(InitialEpisode) start released. Null = everything locked.</summary>
    public int? InitialSeason { get; set; }

    public int? InitialEpisode { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Last time the scheduler evaluated this schedule; cron occurrences after this are due.</summary>
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Add schedules to PluginConfiguration**

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseFin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public ReleaseSchedule[] Schedules { get; set; } = [];
}
```

- [ ] **Step 3: Build**

Run: `dotnet build --nologo` — Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: add ReleaseSchedule configuration model"
```

---

### Task 3: ReleaseFinTag (pure tag math)

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/Core/ReleaseFinTag.cs`
- Test: `tests/Jellyfin.Plugin.ReleaseFin.Tests/ReleaseFinTagTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Jellyfin.Plugin.ReleaseFin.Tests/ReleaseFinTagTests.cs`:

```csharp
using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class ReleaseFinTagTests
{
    private static readonly Guid Sid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void For_BuildsNamespacedTag() =>
        Assert.Equal("releasefin-11111111222233334444555555555555", ReleaseFinTag.For(Sid));

    [Theory]
    [InlineData("releasefin-abc", true)]
    [InlineData("ReleaseFin-abc", true)]
    [InlineData("kids", false)]
    public void IsReleaseFinTag_ChecksPrefix(string tag, bool expected) =>
        Assert.Equal(expected, ReleaseFinTag.IsReleaseFinTag(tag));

    [Fact]
    public void Add_IsIdempotent()
    {
        var once = ReleaseFinTag.Add(["kids"], "releasefin-x");
        var twice = ReleaseFinTag.Add(once, "RELEASEFIN-X");
        Assert.Equal(["kids", "releasefin-x"], once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Remove_IsCaseInsensitive_AndKeepsOtherTags()
    {
        var result = ReleaseFinTag.Remove(["kids", "Releasefin-X"], "releasefin-x");
        Assert.Equal(["kids"], result);
        Assert.Equal(["kids"], ReleaseFinTag.Remove(["kids"], "releasefin-x"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --nologo`
Expected: FAIL — `ReleaseFinTag` does not exist (compile error).

- [ ] **Step 3: Implement**

`src/Jellyfin.Plugin.ReleaseFin/Core/ReleaseFinTag.cs`:

```csharp
namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Tag naming and idempotent tag-list math. The releasefin- prefix is the plugin's
/// ownership boundary: code must never create or remove tags outside it.</summary>
public static class ReleaseFinTag
{
    public const string Prefix = "releasefin-";

    public static string For(Guid scheduleId) => Prefix + scheduleId.ToString("N");

    public static bool IsReleaseFinTag(string tag) =>
        tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string[] Add(string[] tags, string tag) =>
        tags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? tags : [.. tags, tag];

    public static string[] Remove(string[] tags, string tag) =>
        tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --nologo` — Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: releasefin tag naming and idempotent tag math"
```

---

### Task 4: EpisodeKey (pure aired-order logic)

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/Core/EpisodeKey.cs`
- Test: `tests/Jellyfin.Plugin.ReleaseFin.Tests/EpisodeKeyTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Jellyfin.Plugin.ReleaseFin.Tests/EpisodeKeyTests.cs`:

```csharp
using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class EpisodeKeyTests
{
    [Fact]
    public void SortsAiredOrder_SeasonThenEpisode()
    {
        EpisodeKey[] keys = [new(2, 1), new(1, 10), new(1, 2)];
        var sorted = keys.OrderBy(k => k).ToArray();
        Assert.Equal([new(1, 2), new(1, 10), new(2, 1)], sorted);
    }

    [Fact]
    public void Season0_IsSpecial()
    {
        Assert.True(new EpisodeKey(0, 1).IsSpecial);
        Assert.False(new EpisodeKey(1, 1).IsSpecial);
    }

    [Fact]
    public void TryCreate_RejectsMissingNumbers()
    {
        Assert.False(EpisodeKey.TryCreate(null, 1, out _));
        Assert.False(EpisodeKey.TryCreate(1, null, out _));
        Assert.True(EpisodeKey.TryCreate(1, 2, out var key));
        Assert.Equal(new EpisodeKey(1, 2), key);
    }

    [Fact]
    public void IsAtOrBefore_ComparesInAiredOrder()
    {
        Assert.True(new EpisodeKey(1, 5).IsAtOrBefore(new EpisodeKey(1, 5)));
        Assert.True(new EpisodeKey(1, 5).IsAtOrBefore(new EpisodeKey(2, 1)));
        Assert.False(new EpisodeKey(2, 1).IsAtOrBefore(new EpisodeKey(1, 5)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --nologo` — Expected: FAIL, `EpisodeKey` does not exist.

- [ ] **Step 3: Implement**

`src/Jellyfin.Plugin.ReleaseFin/Core/EpisodeKey.cs`:

```csharp
namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Aired-order position of an episode (season, then episode number).</summary>
public readonly record struct EpisodeKey(int Season, int Episode) : IComparable<EpisodeKey>
{
    public bool IsSpecial => Season == 0;

    public int CompareTo(EpisodeKey other) =>
        Season != other.Season ? Season.CompareTo(other.Season) : Episode.CompareTo(other.Episode);

    public bool IsAtOrBefore(EpisodeKey other) => CompareTo(other) <= 0;

    /// <summary>Episodes without both numbers cannot be ordered and are excluded from drip logic.</summary>
    public static bool TryCreate(int? season, int? episode, out EpisodeKey key)
    {
        if (season is null || episode is null)
        {
            key = default;
            return false;
        }

        key = new EpisodeKey(season.Value, episode.Value);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --nologo` — Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: aired-order episode key"
```

---

### Task 5: ScheduleCalculator (pure cron logic)

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/Core/ScheduleCalculator.cs`
- Test: `tests/Jellyfin.Plugin.ReleaseFin.Tests/ScheduleCalculatorTests.cs`

Semantics: due ticks are cron occurrences in the half-open interval `(lastRunUtc, nowUtc]` — exclusive of the last evaluated instant, inclusive of now — evaluated in the given time zone (production passes `TimeZoneInfo.Local` so "0 16 * * *" means 16:00 server time). Tests pass `TimeZoneInfo.Utc` to be machine-independent.

- [ ] **Step 1: Write the failing tests**

`tests/Jellyfin.Plugin.ReleaseFin.Tests/ScheduleCalculatorTests.cs`:

```csharp
using Jellyfin.Plugin.ReleaseFin.Core;
using Xunit;

namespace Jellyfin.Plugin.ReleaseFin.Tests;

public class ScheduleCalculatorTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0 16 * * *", true)]
    [InlineData("0 16 * * 1,3,5", true)]
    [InlineData("not a cron", false)]
    [InlineData("", false)]
    public void IsValid_ChecksCronSyntax(string expr, bool expected) =>
        Assert.Equal(expected, ScheduleCalculator.IsValid(expr));

    [Fact]
    public void CountDueTicks_ZeroWhenNoOccurrenceElapsed() =>
        Assert.Equal(0, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 6, 17, 0), Utc(2026, 7, 7, 15, 0), TimeZoneInfo.Utc));

    [Fact]
    public void CountDueTicks_AccumulatesMissedDays() =>
        // 3 days offline => 3 due ticks ("accumulate freely")
        Assert.Equal(3, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 3, 17, 0), Utc(2026, 7, 6, 17, 0), TimeZoneInfo.Utc));

    [Fact]
    public void CountDueTicks_BoundaryIsExclusiveOfLastRun_InclusiveOfNow() =>
        // lastRun exactly at an occurrence must not double-count it; now at an occurrence counts.
        Assert.Equal(1, ScheduleCalculator.CountDueTicks(
            "0 16 * * *", Utc(2026, 7, 5, 16, 0), Utc(2026, 7, 6, 16, 0), TimeZoneInfo.Utc));

    [Fact]
    public void NextOccurrence_ReturnsUpcomingInstant() =>
        Assert.Equal(
            Utc(2026, 7, 6, 16, 0),
            ScheduleCalculator.NextOccurrenceUtc("0 16 * * *", Utc(2026, 7, 6, 12, 0), TimeZoneInfo.Utc));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --nologo` — Expected: FAIL, `ScheduleCalculator` does not exist.

- [ ] **Step 3: Implement**

`src/Jellyfin.Plugin.ReleaseFin/Core/ScheduleCalculator.cs`:

```csharp
using Cronos;

namespace Jellyfin.Plugin.ReleaseFin.Core;

/// <summary>Cron evaluation. Instants are UTC; the time zone parameter defines what wall-clock
/// time the cron fields refer to (production uses TimeZoneInfo.Local).</summary>
public static class ScheduleCalculator
{
    public static bool IsValid(string expression)
    {
        try
        {
            CronExpression.Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Occurrences in (lastRunUtc, nowUtc]. Missed occurrences all count ("accumulate freely").</summary>
    public static int CountDueTicks(string expression, DateTime lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (nowUtc <= lastRunUtc)
        {
            return 0;
        }

        return CronExpression.Parse(expression)
            .GetOccurrences(lastRunUtc, nowUtc, zone, fromInclusive: false, toInclusive: true)
            .Count();
    }

    public static DateTime? NextOccurrenceUtc(string expression, DateTime nowUtc, TimeZoneInfo zone) =>
        CronExpression.Parse(expression).GetNextOccurrence(nowUtc, zone);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --nologo` — Expected: all pass. If `GetOccurrences` boundary semantics differ from expectation, fix the implementation (not the test): the contract is (exclusive, inclusive].

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: cron schedule calculator with catch-up counting"
```

---

### Task 6: ReleaseManager (Jellyfin glue)

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/ReleaseManager.cs`
- Create: `src/Jellyfin.Plugin.ReleaseFin/PluginServiceRegistrator.cs`

No unit tests (thin glue over verified APIs — see Testing boundary above). Verified by Task 10.

- [ ] **Step 1: Implement ReleaseManager**

`src/Jellyfin.Plugin.ReleaseFin/ReleaseManager.cs`:

```csharp
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Applies drip-release decisions to the library and user policies. All mutations are
/// serialized behind a semaphore so timer ticks, API calls, and library events cannot interleave.
/// SAFETY: only ever creates/removes releasefin-* tags and this plugin's own blocked-tag entries.</summary>
public class ReleaseManager(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILogger<ReleaseManager> logger)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>On schedule creation: lock every episode after the initial offset, then block the tag for each user.</summary>
    public async Task ApplyAsync(ReleaseSchedule schedule, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);
            EpisodeKey? offset = schedule.InitialSeason is int s && schedule.InitialEpisode is int e
                ? new EpisodeKey(s, e)
                : null;

            foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (offset is { } o && key.IsAtOrBefore(o))
                {
                    continue;
                }

                await SetTagAsync(episode, tag, present: true, ct).ConfigureAwait(false);
            }

            await SetUserBlockAsync(schedule.UserIds, tag, blocked: true).ConfigureAwait(false);
            logger.LogInformation("ReleaseFin: applied schedule {Name} ({Id})", schedule.Name, schedule.Id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Release the next N still-locked episodes in aired order. Returns how many were released.</summary>
    public async Task<int> ReleaseNextAsync(ReleaseSchedule schedule, int count, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);
            var released = 0;

            foreach (var (episode, _) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (released >= count)
                {
                    break;
                }

                if (!episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await SetTagAsync(episode, tag, present: false, ct).ConfigureAwait(false);
                released++;
            }

            if (released > 0)
            {
                logger.LogInformation(
                    "ReleaseFin: released {Count} episode(s) for schedule {Name}", released, schedule.Name);
            }

            return released;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>On schedule deletion: untag everything and unblock the tag for all users (not just
    /// currently assigned ones, in case assignments changed).</summary>
    public async Task RemoveAsync(ReleaseSchedule schedule, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = ReleaseFinTag.For(schedule.Id);

            foreach (var (episode, _) in GetOrderedEpisodes(schedule.SeriesId))
            {
                await SetTagAsync(episode, tag, present: false, ct).ConfigureAwait(false);
            }

            var allUserIds = userManager.Users.Select(u => u.Id).ToArray();
            await SetUserBlockAsync(allUserIds, tag, blocked: false).ConfigureAwait(false);
            logger.LogInformation("ReleaseFin: removed schedule {Name} ({Id})", schedule.Name, schedule.Id);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>A newly imported episode arrives locked unless it sorts at or before the released
    /// frontier (the highest untagged, orderable, non-special episode).</summary>
    public async Task LockNewEpisodeAsync(ReleaseSchedule schedule, Episode newEpisode, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!EpisodeKey.TryCreate(newEpisode.ParentIndexNumber, newEpisode.IndexNumber, out var newKey)
                || newKey.IsSpecial)
            {
                return;
            }

            var tag = ReleaseFinTag.For(schedule.Id);
            EpisodeKey? frontier = null;
            foreach (var (episode, key) in GetOrderedEpisodes(schedule.SeriesId))
            {
                if (episode.Id != newEpisode.Id
                    && !episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    frontier = key; // ordered ascending => ends at the highest released key
                }
            }

            if (frontier is { } f && newKey.IsAtOrBefore(f))
            {
                return; // back-fill inside the already-released region stays visible
            }

            await SetTagAsync(newEpisode, tag, present: true, ct).ConfigureAwait(false);
            logger.LogInformation(
                "ReleaseFin: locked new episode S{Season}E{Episode} for schedule {Name}",
                newKey.Season, newKey.Episode, schedule.Name);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>(released, total) drip-eligible episodes; also flags an orphaned (empty) series.</summary>
    public (int Released, int Total) GetProgress(ReleaseSchedule schedule)
    {
        var tag = ReleaseFinTag.For(schedule.Id);
        var episodes = GetOrderedEpisodes(schedule.SeriesId).ToList();
        var locked = episodes.Count(p => p.Episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        return (episodes.Count - locked, episodes.Count);
    }

    /// <summary>Drip-eligible episodes of the series in aired order (orderable, non-special).</summary>
    private IEnumerable<(Episode Episode, EpisodeKey Key)> GetOrderedEpisodes(Guid seriesId) =>
        libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            Recursive = true
        })
        .OfType<Episode>()
        .Select(e => (Episode: e,
            HasKey: EpisodeKey.TryCreate(e.ParentIndexNumber, e.IndexNumber, out var key),
            Key: key))
        .Where(t => t.HasKey && !t.Key.IsSpecial)
        .OrderBy(t => t.Key)
        .Select(t => (t.Episode, t.Key));

    private async Task SetTagAsync(Episode episode, string tag, bool present, CancellationToken ct)
    {
        var updated = present
            ? ReleaseFinTag.Add(episode.Tags, tag)
            : ReleaseFinTag.Remove(episode.Tags, tag);
        if (ReferenceEquals(updated, episode.Tags) || updated.Length == episode.Tags.Length)
        {
            if (present == episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return; // already in desired state; skip a pointless metadata write
            }
        }

        episode.Tags = updated;
        await libraryManager
            .UpdateItemAsync(episode, episode.GetParent(), ItemUpdateType.MetadataEdit, ct)
            .ConfigureAwait(false);
    }

    private async Task SetUserBlockAsync(Guid[] userIds, string tag, bool blocked)
    {
        foreach (var userId in userIds)
        {
            var user = userManager.GetUserById(userId);
            if (user is null)
            {
                continue; // user deleted; nothing to clean
            }

            var current = user.GetPreference(PreferenceKind.BlockedTags) ?? [];
            var updated = blocked ? ReleaseFinTag.Add(current, tag) : ReleaseFinTag.Remove(current, tag);
            if (updated.Length == current.Length)
            {
                continue;
            }

            user.SetPreference(PreferenceKind.BlockedTags, updated);
            await userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
    }
}
```

Note for the implementer: if `GetUserById`, `GetPreference` nullability, or `PreferenceKind`'s namespace don't compile exactly as written, check the 10.10.7 source (`MediaBrowser.Controller/Library/IUserManager.cs`, `Jellyfin.Data/Entities/User.cs`) and adjust the call — the intent (read prefs, modify array, `UpdateUserAsync`) is verified.

**AMENDMENT (post-review, applies to the code above):**
1. `LockNewEpisodeAsync` frontier rule is wrong under multi-episode imports (other just-imported untagged episodes inflate the frontier, leaking a season pack). Replace with: let `firstTagged` = lowest-keyed episode (excluding the new one) still carrying the schedule tag. If `firstTagged` exists, the new episode stays visible only when `newKey.CompareTo(firstTagged) < 0` (back-fill inside the released prefix); otherwise tag it. If no tagged episode exists (drip caught up), always tag — a back-filled old episode self-heals on the next tick.
2. `TickAsync` must persist progress even when a later schedule throws or shutdown cancels mid-loop: wrap the schedule loop in `try { ... } finally { if (changed) plugin.SaveConfiguration(); }`.
3. Only log "locked new episode" when a write actually happened, and fix the `GetProgress` doc comment (it returns counts; it does not flag orphans).

**AMENDMENT 2 (supersedes amendment 1's frontier rule):** the tag-derived firstTagged rule is still order-dependent when nothing is tagged (caught-up drip + season pack processed out of order: the first-processed episode gets tagged, its siblings then read as back-fill against it and leak). Classification of NEW imports must not depend on churning tag state. Fix:
- Add a persisted high-water mark to `ReleaseSchedule`: `public int? ReleasedUpToSeason { get; set; }` / `public int? ReleasedUpToEpisode { get; set; }`.
- `ApplyAsync` initializes it from `InitialSeason`/`InitialEpisode` (null when no offset).
- `ReleaseNextAsync` advances it past each episode it untags (max of current frontier and the released key).
- `LockNewEpisodeAsync` becomes trivial and order-independent: tag the new episode iff the frontier is null OR `newKey` sorts after the frontier; no tag scanning.
- Persistence: the entrypoint already saves config after due ticks; `ReleaseNow` in the controller must also call `Plugin.Instance!.SaveConfiguration()` after `ReleaseNextAsync`.
- The "release pointer is derived, not stored" principle still governs WHICH episode releases next (tag scan); the stored frontier only classifies new imports. Manual admin untagging still works; it simply doesn't advance the import frontier — safe direction (locks, never leaks).

- [ ] **Step 2: Implement the service registrator**

`src/Jellyfin.Plugin.ReleaseFin/PluginServiceRegistrator.cs`:

```csharp
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ReleaseFin;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ReleaseManager>();
    }
}
```

- [ ] **Step 3: Build and run tests**

Run: `dotnet build --nologo && dotnet test --nologo` — Expected: build succeeds, existing tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: release manager applying tags and blocked-tag policies"
```

---

### Task 7: ReleaseFinEntrypoint (scheduler + library watcher)

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/ReleaseFinEntrypoint.cs`
- Modify: `src/Jellyfin.Plugin.ReleaseFin/PluginServiceRegistrator.cs`

- [ ] **Step 1: Implement the hosted service**

`src/Jellyfin.Plugin.ReleaseFin/ReleaseFinEntrypoint.cs`:

```csharp
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseFin;

/// <summary>Runs the drip scheduler (1-minute timer; catch-up after downtime is inherent because
/// due ticks are counted from the persisted LastRunUtc) and locks newly imported episodes.</summary>
public sealed class ReleaseFinEntrypoint(
    ReleaseManager releaseManager,
    ILibraryManager libraryManager,
    ILogger<ReleaseFinEntrypoint> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemAdded;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemAdded;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        await TickAsync(ct).ConfigureAwait(false); // immediate pass = startup catch-up
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var changed = false;
        foreach (var schedule in plugin.Configuration.Schedules)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (!schedule.Enabled || !ScheduleCalculator.IsValid(schedule.CronExpression))
                {
                    continue;
                }

                var due = ScheduleCalculator.CountDueTicks(
                    schedule.CronExpression, schedule.LastRunUtc, now, TimeZoneInfo.Local);
                if (due == 0)
                {
                    continue;
                }

                await releaseManager
                    .ReleaseNextAsync(schedule, due * schedule.EpisodesPerTick, ct)
                    .ConfigureAwait(false);
                schedule.LastRunUtc = now;
                changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ReleaseFin: tick failed for schedule {Name}", schedule.Name);
            }
        }

        if (changed)
        {
            plugin.SaveConfiguration();
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Episode episode || Plugin.Instance is null)
        {
            return;
        }

        var schedules = Plugin.Instance.Configuration.Schedules
            .Where(s => s.Enabled && s.SeriesId == episode.SeriesId)
            .ToArray();
        if (schedules.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var schedule in schedules)
            {
                try
                {
                    await releaseManager.LockNewEpisodeAsync(schedule, episode, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ReleaseFin: failed to lock new episode for {Name}", schedule.Name);
                }
            }
        });
    }

    public void Dispose() => _cts.Dispose();
}
```

- [ ] **Step 2: Register it** — in `PluginServiceRegistrator.RegisterServices`, after the singleton line add:

```csharp
serviceCollection.AddHostedService<ReleaseFinEntrypoint>();
```

- [ ] **Step 3: Build and run tests**

Run: `dotnet build --nologo && dotnet test --nologo` — Expected: green.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: drip scheduler hosted service with startup catch-up and new-episode locking"
```

---

### Task 8: REST API controller

**Files:**
- Create: `src/Jellyfin.Plugin.ReleaseFin/Api/ReleaseFinController.cs`

Endpoints (all admin-only): list schedules with status, create, update, delete, release-now, cron preview. The UI reads users/series through Jellyfin's existing endpoints (`ApiClient.getUsers()`, `/Items?IncludeItemTypes=Series`) — no custom endpoints for those (YAGNI).

- [ ] **Step 1: Implement the controller**

`src/Jellyfin.Plugin.ReleaseFin/Api/ReleaseFinController.cs`:

```csharp
using System.Net.Mime;
using Jellyfin.Plugin.ReleaseFin.Configuration;
using Jellyfin.Plugin.ReleaseFin.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ReleaseFin.Api;

public class ScheduleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid SeriesId { get; set; }

    public string SeriesName { get; set; } = string.Empty;

    public Guid[] UserIds { get; set; } = [];

    public string CronExpression { get; set; } = string.Empty;

    public int EpisodesPerTick { get; set; }

    public int? InitialSeason { get; set; }

    public int? InitialEpisode { get; set; }

    public bool Enabled { get; set; }

    public int Released { get; set; }

    public int Total { get; set; }

    public DateTime? NextRunUtc { get; set; }

    public bool Orphaned { get; set; }
}

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ReleaseFin")]
[Produces(MediaTypeNames.Application.Json)]
public class ReleaseFinController(ReleaseManager releaseManager, ILibraryManager libraryManager)
    : ControllerBase
{
    [HttpGet("Schedules")]
    public ActionResult<IEnumerable<ScheduleDto>> GetSchedules() =>
        Ok(Config.Schedules.Select(ToDto));

    [HttpPost("Schedules")]
    public async Task<ActionResult<ScheduleDto>> Create([FromBody] ReleaseSchedule schedule, CancellationToken ct)
    {
        var error = Validate(schedule);
        if (error is not null)
        {
            return BadRequest(error);
        }

        schedule.Id = Guid.NewGuid();
        schedule.LastRunUtc = DateTime.UtcNow;
        await releaseManager.ApplyAsync(schedule, ct).ConfigureAwait(false);
        Config.Schedules = [.. Config.Schedules, schedule];
        Plugin.Instance!.SaveConfiguration();
        return Ok(ToDto(schedule));
    }

    [HttpPut("Schedules/{id}")]
    public async Task<ActionResult<ScheduleDto>> Update(Guid id, [FromBody] ReleaseSchedule updated, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        var error = Validate(updated);
        if (error is not null)
        {
            return BadRequest(error);
        }

        // Simplest correct semantics: tear down the old assignment, apply the new one.
        // Already-released episodes get re-locked if they're past the new offset — acceptable for edits.
        await releaseManager.RemoveAsync(existing, ct).ConfigureAwait(false);
        updated.Id = id;
        updated.LastRunUtc = DateTime.UtcNow;
        await releaseManager.ApplyAsync(updated, ct).ConfigureAwait(false);
        Config.Schedules = [.. Config.Schedules.Where(s => s.Id != id), updated];
        Plugin.Instance!.SaveConfiguration();
        return Ok(ToDto(updated));
    }

    [HttpDelete("Schedules/{id}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        await releaseManager.RemoveAsync(existing, ct).ConfigureAwait(false);
        Config.Schedules = Config.Schedules.Where(s => s.Id != id).ToArray();
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    [HttpPost("Schedules/{id}/ReleaseNow")]
    public async Task<ActionResult<ScheduleDto>> ReleaseNow(Guid id, CancellationToken ct)
    {
        var existing = Config.Schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        await releaseManager.ReleaseNextAsync(existing, existing.EpisodesPerTick, ct).ConfigureAwait(false);
        return Ok(ToDto(existing));
    }

    [HttpGet("CronPreview")]
    public ActionResult<IEnumerable<DateTime>> CronPreview([FromQuery] string expression)
    {
        if (!ScheduleCalculator.IsValid(expression))
        {
            return BadRequest("Invalid cron expression.");
        }

        var occurrences = new List<DateTime>(3);
        var cursor = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var next = ScheduleCalculator.NextOccurrenceUtc(expression, cursor, TimeZoneInfo.Local);
            if (next is null)
            {
                break;
            }

            occurrences.Add(next.Value);
            cursor = next.Value;
        }

        return Ok(occurrences);
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string? Validate(ReleaseSchedule s)
    {
        if (!ScheduleCalculator.IsValid(s.CronExpression))
        {
            return "Invalid cron expression.";
        }

        if (s.SeriesId == Guid.Empty)
        {
            return "A series must be selected.";
        }

        if (s.UserIds.Length == 0)
        {
            return "At least one user must be selected.";
        }

        if (s.EpisodesPerTick < 1)
        {
            return "Episodes per tick must be at least 1.";
        }

        return null;
    }

    private ScheduleDto ToDto(ReleaseSchedule s)
    {
        var (released, total) = releaseManager.GetProgress(s);
        var series = libraryManager.GetItemById(s.SeriesId);
        return new ScheduleDto
        {
            Id = s.Id,
            Name = s.Name,
            SeriesId = s.SeriesId,
            SeriesName = series?.Name ?? "(deleted series)",
            UserIds = s.UserIds,
            CronExpression = s.CronExpression,
            EpisodesPerTick = s.EpisodesPerTick,
            InitialSeason = s.InitialSeason,
            InitialEpisode = s.InitialEpisode,
            Enabled = s.Enabled,
            Released = released,
            Total = total,
            NextRunUtc = ScheduleCalculator.IsValid(s.CronExpression)
                ? ScheduleCalculator.NextOccurrenceUtc(s.CronExpression, DateTime.UtcNow, TimeZoneInfo.Local)
                : null,
            Orphaned = series is null
        };
    }
}
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet build --nologo && dotnet test --nologo` — Expected: green.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: admin REST API for schedules"
```

---

### Task 9: Admin config page

**Files:**
- Modify: `src/Jellyfin.Plugin.ReleaseFin/Configuration/configPage.html` (replace placeholder)

Requirements from the spec: schedule list (series, users, next fire, "12/48 released", orphaned flag), create/edit form (series picker, user multi-select, daily/weekly preset builder + advanced raw-cron toggle, initial offset, episodes/tick), actions (enable/disable via edit, release-now, delete with confirm), cron preview from `GET ReleaseFin/CronPreview`. Uses the dashboard's global `ApiClient` and `Dashboard` objects; no external assets. Presets compile to cron client-side; the cron string is what's stored.

- [ ] **Step 1: Implement the page**

Replace `configPage.html` entirely with:

```html
<!DOCTYPE html>
<html>
<head>
    <title>ReleaseFin</title>
</head>
<body>
<div id="ReleaseFinConfigPage" data-role="page" class="page type-interior pluginConfigurationPage"
     data-require="emby-input,emby-button,emby-select,emby-checkbox">
    <div data-role="content">
        <div class="content-primary">
            <h1>ReleaseFin — Episode Release Schedules</h1>
            <div id="rfScheduleList"></div>
            <br/>
            <button is="emby-button" type="button" id="rfNewSchedule" class="raised button-submit">
                <span>New schedule</span>
            </button>

            <form id="rfForm" style="display:none; margin-top:2em;">
                <h2 id="rfFormTitle">New schedule</h2>
                <input type="hidden" id="rfId"/>
                <div class="inputContainer">
                    <input is="emby-input" type="text" id="rfName" label="Name" required/>
                </div>
                <div class="selectContainer">
                    <select is="emby-select" id="rfSeries" label="Series" required></select>
                </div>
                <div class="inputContainer">
                    <label>Users</label>
                    <div id="rfUsers"></div>
                </div>
                <div class="selectContainer">
                    <select is="emby-select" id="rfPreset" label="Schedule">
                        <option value="daily">Daily</option>
                        <option value="weekly">Weekly</option>
                        <option value="cron">Advanced (raw cron)</option>
                    </select>
                </div>
                <div class="selectContainer" id="rfWeekdayRow">
                    <select is="emby-select" id="rfWeekday" label="Weekday">
                        <option value="1">Monday</option><option value="2">Tuesday</option>
                        <option value="3">Wednesday</option><option value="4">Thursday</option>
                        <option value="5">Friday</option><option value="6">Saturday</option>
                        <option value="0">Sunday</option>
                    </select>
                </div>
                <div class="inputContainer" id="rfTimeRow">
                    <input is="emby-input" type="time" id="rfTime" label="Time of day" value="16:00"/>
                </div>
                <div class="inputContainer" id="rfCronRow" style="display:none;">
                    <input is="emby-input" type="text" id="rfCron" label="Cron expression (min hour dom mon dow)"/>
                </div>
                <div id="rfCronPreview" class="fieldDescription"></div>
                <div class="inputContainer">
                    <input is="emby-input" type="number" id="rfPerTick" label="Episodes per release" value="1" min="1"/>
                </div>
                <div class="inputContainer">
                    <input is="emby-input" type="text" id="rfOffset"
                           label="Already released up to (e.g. S01E05, empty = nothing)" placeholder="S01E05"/>
                </div>
                <label class="checkboxContainer">
                    <input is="emby-checkbox" type="checkbox" id="rfEnabled" checked/>
                    <span>Enabled</span>
                </label>
                <br/>
                <button is="emby-button" type="submit" class="raised button-submit"><span>Save</span></button>
                <button is="emby-button" type="button" id="rfCancel" class="raised"><span>Cancel</span></button>
            </form>
        </div>
    </div>

    <script type="text/javascript">
    (function () {
        var page = document.querySelector('#ReleaseFinConfigPage');
        var url = function (path) { return ApiClient.getUrl('ReleaseFin/' + path); };

        function presetToCron() {
            var preset = page.querySelector('#rfPreset').value;
            if (preset === 'cron') return page.querySelector('#rfCron').value.trim();
            var t = (page.querySelector('#rfTime').value || '16:00').split(':');
            var minute = parseInt(t[1], 10), hour = parseInt(t[0], 10);
            if (preset === 'daily') return minute + ' ' + hour + ' * * *';
            return minute + ' ' + hour + ' * * ' + page.querySelector('#rfWeekday').value;
        }

        function cronToForm(cron) {
            var m = /^(\d+) (\d+) \* \* (\*|\d)$/.exec(cron);
            var preset = page.querySelector('#rfPreset');
            if (!m) {
                preset.value = 'cron';
                page.querySelector('#rfCron').value = cron;
            } else {
                preset.value = m[3] === '*' ? 'daily' : 'weekly';
                if (m[3] !== '*') page.querySelector('#rfWeekday').value = m[3];
                page.querySelector('#rfTime').value =
                    ('0' + m[2]).slice(-2) + ':' + ('0' + m[1]).slice(-2);
            }
            syncPresetRows();
        }

        function syncPresetRows() {
            var preset = page.querySelector('#rfPreset').value;
            page.querySelector('#rfCronRow').style.display = preset === 'cron' ? '' : 'none';
            page.querySelector('#rfTimeRow').style.display = preset === 'cron' ? 'none' : '';
            page.querySelector('#rfWeekdayRow').style.display = preset === 'weekly' ? '' : 'none';
            updatePreview();
        }

        function updatePreview() {
            var cron = presetToCron();
            var out = page.querySelector('#rfCronPreview');
            if (!cron) { out.textContent = ''; return; }
            ApiClient.ajax({ type: 'GET', url: url('CronPreview?expression=' + encodeURIComponent(cron)), dataType: 'json' })
                .then(function (dates) {
                    out.textContent = 'Next releases: ' + dates.map(function (d) {
                        return new Date(d + (d.endsWith('Z') ? '' : 'Z')).toLocaleString();
                    }).join(', ');
                })
                .catch(function () { out.textContent = 'Invalid cron expression.'; });
        }

        function parseOffset(text) {
            if (!text) return { season: null, episode: null };
            var m = /^S(\d+)E(\d+)$/i.exec(text.trim());
            if (!m) throw new Error('Offset must look like S01E05');
            return { season: parseInt(m[1], 10), episode: parseInt(m[2], 10) };
        }

        function loadPickers() {
            return Promise.all([
                ApiClient.getUsers(),
                ApiClient.getItems(ApiClient.getCurrentUserId(),
                    { IncludeItemTypes: 'Series', Recursive: true, SortBy: 'SortName' })
            ]).then(function (results) {
                page.querySelector('#rfUsers').innerHTML = results[0].map(function (u) {
                    return '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" ' +
                        'class="rfUser" value="' + u.Id + '"/><span>' + u.Name + '</span></label>';
                }).join('');
                page.querySelector('#rfSeries').innerHTML = results[1].Items.map(function (s) {
                    return '<option value="' + s.Id + '">' + s.Name + '</option>';
                }).join('');
            });
        }

        function loadList() {
            Dashboard.showLoadingMsg();
            ApiClient.ajax({ type: 'GET', url: url('Schedules'), dataType: 'json' }).then(function (schedules) {
                var html = schedules.length === 0 ? '<p>No schedules yet.</p>' :
                    '<table class="detailTable"><thead><tr><th>Name</th><th>Series</th><th>Progress</th>' +
                    '<th>Next release</th><th>Status</th><th></th></tr></thead><tbody>' +
                    schedules.map(function (s) {
                        var status = s.Orphaned ? 'ORPHANED (series deleted)'
                            : !s.Enabled ? 'Disabled'
                            : s.Released >= s.Total ? 'Complete' : 'Active';
                        return '<tr><td>' + s.Name + '</td><td>' + s.SeriesName + '</td>' +
                            '<td>' + s.Released + '/' + s.Total + ' released</td>' +
                            '<td>' + (s.NextRunUtc ? new Date(s.NextRunUtc + 'Z').toLocaleString() : '—') + '</td>' +
                            '<td>' + status + '</td>' +
                            '<td><button is="emby-button" type="button" class="raised rfEdit" data-id="' + s.Id + '">Edit</button> ' +
                            '<button is="emby-button" type="button" class="raised rfRelease" data-id="' + s.Id + '">Release now</button> ' +
                            '<button is="emby-button" type="button" class="raised button-delete rfDelete" data-id="' + s.Id + '">Delete</button></td></tr>';
                    }).join('') + '</tbody></table>';
                page.querySelector('#rfScheduleList').innerHTML = html;
                page._schedules = schedules;
                Dashboard.hideLoadingMsg();
            });
        }

        function showForm(schedule) {
            page.querySelector('#rfForm').style.display = '';
            page.querySelector('#rfFormTitle').textContent = schedule ? 'Edit schedule' : 'New schedule';
            page.querySelector('#rfId').value = schedule ? schedule.Id : '';
            page.querySelector('#rfName').value = schedule ? schedule.Name : '';
            page.querySelector('#rfPerTick').value = schedule ? schedule.EpisodesPerTick : 1;
            page.querySelector('#rfEnabled').checked = schedule ? schedule.Enabled : true;
            page.querySelector('#rfOffset').value = schedule && schedule.InitialSeason !== null
                ? 'S' + ('0' + schedule.InitialSeason).slice(-2) + 'E' + ('0' + schedule.InitialEpisode).slice(-2) : '';
            if (schedule) page.querySelector('#rfSeries').value = schedule.SeriesId;
            Array.prototype.forEach.call(page.querySelectorAll('.rfUser'), function (cb) {
                cb.checked = !!schedule && schedule.UserIds.indexOf(cb.value) !== -1;
            });
            cronToForm(schedule ? schedule.CronExpression : '0 16 * * *');
        }

        function hideForm() { page.querySelector('#rfForm').style.display = 'none'; }

        page.addEventListener('pageshow', function () {
            loadPickers().then(loadList);
        });

        page.querySelector('#rfPreset').addEventListener('change', syncPresetRows);
        page.querySelector('#rfTime').addEventListener('change', updatePreview);
        page.querySelector('#rfWeekday').addEventListener('change', updatePreview);
        page.querySelector('#rfCron').addEventListener('input', updatePreview);
        page.querySelector('#rfNewSchedule').addEventListener('click', function () { showForm(null); });
        page.querySelector('#rfCancel').addEventListener('click', hideForm);

        page.querySelector('#rfScheduleList').addEventListener('click', function (e) {
            var btn = e.target.closest('button');
            if (!btn) return;
            var id = btn.getAttribute('data-id');
            if (btn.classList.contains('rfEdit')) {
                showForm(page._schedules.find(function (s) { return s.Id === id; }));
            } else if (btn.classList.contains('rfRelease')) {
                ApiClient.ajax({ type: 'POST', url: url('Schedules/' + id + '/ReleaseNow') }).then(loadList);
            } else if (btn.classList.contains('rfDelete')) {
                if (window.confirm('Delete this schedule? All its episodes become visible again.')) {
                    ApiClient.ajax({ type: 'DELETE', url: url('Schedules/' + id) }).then(loadList);
                }
            }
        });

        page.querySelector('#rfForm').addEventListener('submit', function (e) {
            e.preventDefault();
            var offset;
            try { offset = parseOffset(page.querySelector('#rfOffset').value); }
            catch (err) { Dashboard.alert(err.message); return; }
            var id = page.querySelector('#rfId').value;
            var body = {
                Name: page.querySelector('#rfName').value,
                SeriesId: page.querySelector('#rfSeries').value,
                UserIds: Array.prototype.filter.call(page.querySelectorAll('.rfUser'),
                    function (cb) { return cb.checked; }).map(function (cb) { return cb.value; }),
                CronExpression: presetToCron(),
                EpisodesPerTick: parseInt(page.querySelector('#rfPerTick').value, 10) || 1,
                InitialSeason: offset.season,
                InitialEpisode: offset.episode,
                Enabled: page.querySelector('#rfEnabled').checked
            };
            ApiClient.ajax({
                type: id ? 'PUT' : 'POST',
                url: url('Schedules' + (id ? '/' + id : '')),
                data: JSON.stringify(body),
                contentType: 'application/json'
            }).then(function () { hideForm(); loadList(); })
              .catch(function (xhr) {
                  (xhr.text ? xhr.text() : Promise.resolve('Save failed.'))
                      .then(function (t) { Dashboard.alert(t || 'Save failed.'); });
              });
        });
    })();
    </script>
</div>
</body>
</html>
```

Implementer note: `window.confirm` is acceptable in the Jellyfin dashboard context; if the project later wants native dialogs, switch to `Dashboard.confirm`. If `ApiClient.getItems` signature differs, use `ApiClient.getJSON(ApiClient.getUrl('Items', {...}))` — check another plugin's configPage.html for the working idiom.

- [ ] **Step 2: Build**

Run: `dotnet build --nologo` — Expected: succeeds (embedded resource compiles in).

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: admin dashboard configuration page"
```

---

### Task 10: Integration verification (manual, dockerized)

**Files:**
- Create: `dev/docker-compose.yml`
- Create: `dev/README.md`

- [ ] **Step 1: Create the dev environment**

`dev/docker-compose.yml`:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:10.10.7
    ports:
      - "8096:8096"
    volumes:
      - ./config:/config
      - ./media:/media
```

`dev/README.md`:

```markdown
# ReleaseFin dev environment

1. Build & deploy the plugin (Cronos.dll must be copied too):
   dotnet publish src/Jellyfin.Plugin.ReleaseFin -c Release -o /tmp/rf-publish
   mkdir -p dev/config/plugins/ReleaseFin
   cp /tmp/rf-publish/Jellyfin.Plugin.ReleaseFin.dll /tmp/rf-publish/Cronos.dll dev/config/plugins/ReleaseFin/
2. Put a small TV library under dev/media (a couple of series, 2 seasons each; empty
   video files with correct S01E01 naming are enough for metadata-only testing).
3. docker compose -f dev/docker-compose.yml up -d and complete setup at http://localhost:8096
   (create an admin and a "Kids" user).
4. Restart the container after every plugin redeploy.
```

- [ ] **Step 2: Run the spec's verification checklist** (all must pass; record results in the PR/commit message)

1. Dashboard → Plugins → ReleaseFin: assign a daily schedule to a series for Kids with offset S01E02 → as Kids (web + one other client), only S01E01–S01E02 visible; as admin, everything visible with `releasefin-*` tags in the metadata editor.
2. "Release now" → exactly one more episode appears for Kids.
3. Stop the container, move `LastRunUtc` back 3 days in `config/plugins/configurations/ReleaseFin.xml` (or wait across ticks), start → 3 episodes released.
4. Add a new episode file to the scheduled series, run a library scan → it arrives hidden for Kids.
5. Delete the schedule → all episodes visible for Kids; no `releasefin-*` tags remain on items; Kids' parental "block items with tags" list has no ReleaseFin entries.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "chore: dockerized integration test environment and checklist"
```

---

### Task 11: CI, README, release packaging

**Files:**
- Create: `.github/workflows/build.yml`
- Modify: `README.md`
- Create: `build.yaml` (jellyfin plugin build manifest)

- [ ] **Step 1: CI workflow**

`.github/workflows/build.yml`:

```yaml
name: build
on:
  push:
    branches: [main]
  pull_request:
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet build --nologo -warnaserror
      - run: dotnet test --nologo
```

- [ ] **Step 2: Plugin build manifest** (used by jellyfin-plugin-repository tooling / jprm)

`build.yaml`:

```yaml
name: "ReleaseFin"
guid: "e7d1f0a4-8c3b-4a5e-9f2d-6b0c4d8e1a23"
version: "1.0.0.0"
targetAbi: "10.10.0.0"
framework: "net8.0"
owner: "detair"
overview: "Drip-release episodes to selected accounts on a schedule."
description: >
  Assign cron-style release schedules to series for selected accounts (e.g. Kids).
  Unreleased episodes are hidden via per-schedule tags and the users' blocked-tags
  parental control; the scheduler reveals the next episode as each release time passes.
category: "General"
artifacts:
  - "Jellyfin.Plugin.ReleaseFin.dll"
  - "Cronos.dll"
```

- [ ] **Step 3: README** — replace body with: what it does (one paragraph + Kids example), install (manual DLL copy incl. Cronos.dll; plugin repo "coming later"), usage walkthrough (create schedule: series, users, preset vs raw cron, offset, release-now), how hiding works (blocked tags; admins see `releasefin-*` tags in the metadata editor), uninstall/cleanup (delete all schedules first, then uninstall), FAQ (series with zero released episodes looks empty; shared schedules advance together; missed days accumulate). Dispatch the docs-writer agent for this step — it has the full brief in its agent definition.

- [ ] **Step 4: Build, test, commit**

```bash
dotnet build --nologo && dotnet test --nologo
git add -A && git commit -m "chore: CI workflow, plugin build manifest, user docs"
```

---

## Self-review (done at plan-writing time)

- **Spec coverage:** enforcement (T3/T6), derived pointer (T6 ReleaseNext scans tags), accumulate-freely + downtime catch-up (T5/T7), configurable offset (T2/T6), presets+cron UI (T9), completeness/orphan/user-deleted edge cases (T6 GetUserById null-skip, T8 Orphaned flag, list status), release-now (T8/T9), cleanup on delete (T6 RemoveAsync clears ALL users), new-episode locking (T6/T7), CI+docs+manifest (T11), integration checklist (T10). Uninstall cleanup = delete schedules (documented in README, T11); no separate "purge" button — YAGNI for v1.
- **Placeholders:** none; every code step has full code. Two explicitly-marked "verify against source if compile differs" notes are escape hatches on verified-intent APIs, not gaps.
- **Type consistency:** `EpisodeKey.TryCreate(int?, int?, out EpisodeKey)`, `ReleaseFinTag.For(Guid)`, `ScheduleCalculator.CountDueTicks(string, DateTime, DateTime, TimeZoneInfo)` used identically across T4–T8; `ReleaseSchedule` fields match T2 in T8/T9 (JSON casing: ASP.NET camel-cases by default — Jellyfin configures PascalCase JSON; the UI uses PascalCase keys, matching Jellyfin's API convention).
