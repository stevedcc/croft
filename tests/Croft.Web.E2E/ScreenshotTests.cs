using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// The screenshot matrix for the visual review (spec §9: review only, not asserted): every surface
/// at 2560x1440, 1920x1080, 1440x900, 1100x800 and 390x844 in light and dark, saved to
/// TestContext.CurrentContext.WorkDirectory/screenshots/{surface}-{width}-{theme}.png. Brief 03l
/// adds the post page at 1280x800 and 1707x960 (a 4K monitor at 150% scaling) in light. Ruling
/// R5-2-a bounds the assertions to what is knowable: the capture completes and its PNG lands
/// non-empty — pixel content is deliberately not asserted. Ruling R5-2-b defines the surfaces as
/// the superset: Home, a Post, and "the Editor" = the Posts list, the post editor, and the Editor
/// role's list and draft review.
///
/// Each capture gets its own browser context (its own viewport and theme preference), so
/// authenticated surfaces sign in on the context's own page. Both themes set the blog-theme
/// preference before load (the established mechanism) and dark waits for the rendered dark
/// palette — the light first-paint flash (spec §4.3) is not the state to review.
///
/// Fixtures are one run-keyed set shared by every capture and created through the admin UI (the
/// suite's fixture rule): a published post carrying the long-text shapes — 300-char title and
/// subtitle, 20 tags, a banner and a multi-paragraph body (item 3: enough text to show the 88ch
/// column at 1920/2560) — a draft with every optional field missing, the banner-less no-chip
/// shapes the captures must show at least once, and a published rhythm post (the post body's
/// vertical-rhythm shapes) for the post-rhythm captures. The Posts-list captures shoot
/// the default unfiltered view (ruling R5-2-d — visual-review evidence of the designed §6.1 page);
/// the Draft filter is only the fixture-reach device for row assertions.
///
/// Stays ParallelScope.None while the rest of the suite runs fixtures in parallel: the captures
/// write fixed-name PNGs ({surface}-{width}-{theme}.png) into the NUnit work directory shared by
/// the whole run, and the run-keyed fixture data is process-wide static state (_dataReady,
/// DataGate, _postSlug/_draftId/_draftSlug). Running this fixture exclusively — NUnit never runs a
/// ParallelScope.None fixture alongside any other test — keeps the documented screenshot paths for
/// the visual review and needs no per-worker folders; it still gets its own pinned instance for
/// its lifetime like every other fixture (see WebTest.StackUrls).
/// </summary>
[Parallelizable(ParallelScope.None)]
[Category("Screenshots")]
public class ScreenshotTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Exactly 300 characters so the captures show the long-title clamp.</summary>
    private static readonly string LongTitle = Padded($"R52 Shot {RunId} ");

    /// <summary>Exactly 300 characters so the captures show the long-subtitle clamp.</summary>
    private static readonly string LongSubtitle = Padded($"Subtitle {RunId} ");

    private static readonly string TagList =
        string.Join(", ", Enumerable.Range(1, 20).Select(n => $"r52{RunId}t{n:00}"));

    /// <summary>The draft behind the editing surfaces: every optional field missing by construction.</summary>
    private static readonly string DraftTitle = $"R52 Draft {RunId}";

    /// <summary>The post behind the post-rhythm captures (the post body's vertical rhythm).</summary>
    private static readonly string RhythmTitle = $"R52 Rhythm {RunId}";

    private static bool _dataReady;
    private static string _postSlug = string.Empty;
    private static Guid _draftId;
    private static string _draftSlug = string.Empty;
    private static string _rhythmSlug = string.Empty;

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Home(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("home", width, height, theme, null, async page =>
        {
            await page.GotoAsync(BaseUrl);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Latest Posts" }))
                .ToBeVisibleAsync();
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
        });

        AssertCaptureLanded("home", width, theme);
    }

    [TestCase(1280, 800, "light")]
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1707, 960, "light")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Post(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("post", width, height, theme, null, async page =>
        {
            await page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator(".post-hero__title")).ToHaveTextAsync(LongTitle);
            await Expect(page.Locator(".markdown-body")).ToBeVisibleAsync();
        });

        AssertCaptureLanded("post", width, theme);
    }

    /// <summary>The post body's vertical rhythm at the two review widths, both themes: the rhythm
    /// fixture post (two paragraphs, an h2, a tight list of three single-line items wrapping a
    /// nested list, an h3 and a code block) — the body's spacing, reviewed by eye.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task PostRhythm(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("post-rhythm", width, height, theme, null, async page =>
        {
            await page.GotoAsync($"{BaseUrl}/post/{_rhythmSlug}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator(".markdown-body")).ToBeVisibleAsync();
        });

        AssertCaptureLanded("post-rhythm", width, theme);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task PostsList(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("posts-list", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Posts", Level = 1 }))
                .ToBeVisibleAsync();
            // Ruling R5-2-d: the captures shoot the default unfiltered grid — the owner's visual-
            // review evidence of the designed §6.1 page. The Draft filter is only this suite's
            // fixture-reach device for row assertions and stays with those (and the axe runs).
            await Expect(page.Locator(".posts-grid__row").First).ToBeVisibleAsync();
        });

        AssertCaptureLanded("posts-list", width, theme);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task PostEditor(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("post-editor", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts/{_draftId}/edit");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit Post", Level = 1 }))
                .ToBeVisibleAsync();
            await Expect(page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }))
                .ToHaveValueAsync(DraftTitle);
        });

        AssertCaptureLanded("post-editor", width, theme);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task EditorList(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("editor-list", width, height, theme, "editor@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/editor");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Drafts", Level = 1 }))
                .ToBeVisibleAsync();
            await Expect(RowFor(page, DraftTitle)).ToBeVisibleAsync();
        });

        AssertCaptureLanded("editor-list", width, theme);
    }

    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(1920, 1080, "light")]
    [TestCase(1920, 1080, "dark")]
    [TestCase(2560, 1440, "light")]
    [TestCase(2560, 1440, "dark")]
    [TestCase(1100, 800, "light")]
    [TestCase(1100, 800, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task EditorReview(int width, int height, string theme)
    {
        await EnsureFixturesAsync();
        await CaptureAsync("editor-review", width, height, theme, "editor@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/editor/posts/{_draftSlug}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator(".post-hero__ribbon")).ToHaveTextAsync("Draft");
            await Expect(page.Locator(".post-hero__title")).ToHaveTextAsync(DraftTitle);
            await Expect(page.Locator(".markdown-body")).ToBeVisibleAsync();
        });

        AssertCaptureLanded("editor-review", width, theme);
    }

    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private static readonly Dictionary<string, string> SignedInStates = [];

    /// <summary>The browser storage state (cookies) of <paramref name="email"/> signed in through the
    /// mock IdP, taken once per instance and role and reused by every capture of that role.</summary>
    private async Task<string> SignedInStateAsync(string email)
    {
        var key = $"{BaseUrl} {email}";
        await StateGate.WaitAsync();
        try
        {
            if (SignedInStates.TryGetValue(key, out var known))
            {
                return known;
            }

            await using var context = await Browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await LoginAsync(page, email, "password");
            return SignedInStates[key] = await context.StorageStateAsync();
        }
        finally
        {
            StateGate.Release();
        }
    }

    /// <summary>Opens the surface in its own context (viewport + theme) and captures it full-page.</summary>
    private async Task CaptureAsync(
        string surface, int width, int height, string theme, string? signIn,
        Func<IPage, Task> renderAsync, Func<IPage, ILocator>? element = null)
    {
        // The sign-in happens once per role (SignedInStateAsync): 50 of the 66 captures are of
        // signed-in surfaces, and each used to go through the IdP in a fresh context to get the
        // same session. The context starts with that role's cookies instead.
        var storageState = signIn is null ? null : await SignedInStateAsync(signIn);
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ViewportSize = new() { Width = width, Height = height },
            StorageState = storageState,
        });
        await context.AddInitScriptAsync($"localStorage.setItem('blog-theme', '{theme}')");
        var page = await context.NewPageAsync();

        await renderAsync(page);
        if (theme == "dark")
        {
            await WaitForDarkPaletteAsync(page);
        }

        // Start from a clean slate: a stale PNG from an earlier run must never satisfy this
        // run's assertion that the capture landed.
        var path = ScreenshotPath(surface, width, theme);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        // element: shoot that element only (the discussion section) instead of the whole page.
        var png = element is null
            ? await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, FullPage = true })
            : await element(page).ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Png });
        File.WriteAllBytes(path, png);
    }

    /// <summary>The discussion section under a post (brief 12b), for the owner to look at: both platforms' replies, a folded
    /// content warning, a nested answer, a reply with media, a stored avatar and the fallback initial, and the reply actions.
    /// The post is seeded straight into the replies tables (no platform is called; see <see cref="DiscussionSeed"/>).</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task Discussion(int width, int height, string theme)
    {
        await EnsureDiscussionAsync();
        await CaptureAsync("discussion", width, height, theme, null, async page =>
        {
            await page.GotoAsync($"{BaseUrl}/post/{_discussionSlug}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator("#discussion .discussion-reply__name").First).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("() => [...document.querySelectorAll('#discussion img')].every(i => i.complete && i.naturalWidth > 0)");
        }, element: page => page.Locator("#discussion"));

        AssertCaptureLanded("discussion", width, theme);
    }

    /// <summary>The Discussion tab of the post editor (brief 12c): the fetch status, Refresh now, and every stored reply with Hide reply and
    /// Hide account, on the seeded discussion post of the 12b captures. The tab's panel is the element shot.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task DiscussionTab(int width, int height, string theme)
    {
        await EnsureDiscussionAsync();
        await CaptureAsync("discussion-tab", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts/{_discussionPostId}/edit");
            await WaitForInteractiveAsync(page);
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
            await Expect(page.Locator(".discussion-admin__reply").First).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("() => [...document.querySelectorAll('.discussion-admin img')].every(i => i.complete && i.naturalWidth > 0)");
        }, element: page => page.Locator(".post-edit__side"));

        AssertCaptureLanded("discussion-tab", width, theme);
    }

    /// <summary>The announcement fields of the post editor (brief 12d): the two flags, the text with a hashtag, and both previews with their counts.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task AnnouncementFields(int width, int height, string theme)
    {
        await EnsureAnnouncingAsync();
        await CaptureAsync("announcement-fields", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts/{_announcingPostId}/edit");
            await WaitForInteractiveAsync(page);
            await page.GetByLabel("Announcement text").FillAsync("Load testing the homelab: what a small cluster can take. #homelab #dotnet");
            await Expect(page.Locator("#announce-preview-bluesky .announce-preview__count")).ToContainTextAsync("/ 300");
        }, element: page => page.Locator(".post-form__announcement"));

        AssertCaptureLanded("announcement-fields", width, theme);
    }

    /// <summary>The Announcements block on the Discussion tab (brief 12d), with a posted and a failed row seeded into the outbox.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task AnnouncementsBlock(int width, int height, string theme)
    {
        await EnsureAnnouncingAsync();
        await CaptureAsync("announcements-block", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts/{_announcingPostId}/edit");
            await WaitForInteractiveAsync(page);
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Discussion" }).ClickAsync();
            await Expect(page.Locator("#announcement-mastodon a")).ToBeVisibleAsync();
        }, element: page => page.Locator(".discussion-admin__announcements"));

        AssertCaptureLanded("announcements-block", width, theme);
    }

    private static readonly SemaphoreSlim AnnouncingGate = new(1, 1);
    private static Guid _announcingPostId;

    private async Task EnsureAnnouncingAsync()
    {
        await AnnouncingGate.WaitAsync();
        try
        {
            if (_announcingPostId != Guid.Empty)
            {
                return;
            }

            var runId = Guid.NewGuid().ToString("N")[..12];
            var (id, _) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Announcing capture {runId}", $"A short post about a homelab {runId}.");
            await using var db = await OpenInstanceDatabaseAsync();
            await AnnouncementSeed.AddAsync(db, id, runId);
            _announcingPostId = id;
        }
        finally
        {
            AnnouncingGate.Release();
        }
    }

    /// <summary>The announcement previews with the default text and the hashtags of the post's category (brief 12e): the editor's section, and the Tags &amp; categories row where they are edited.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task AnnouncementHashtags(int width, int height, string theme)
    {
        await EnsureHashtagsAsync();
        await CaptureAsync("announcement-hashtags", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/posts/{_hashtagPostId}/edit");
            await WaitForInteractiveAsync(page);
            await Expect(page.Locator("#announce-preview-bluesky .announce-preview__text")).ToContainTextAsync("#Homelab #SelfHosted");
        }, element: page => page.Locator(".post-form__announcement"));

        AssertCaptureLanded("announcement-hashtags", width, theme);

        await CaptureAsync("tags-hashtags", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/tags");
            await WaitForInteractiveAsync(page);
            var search = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Search categories" });
            await search.FillAsync(_hashtagCategory);
            await search.PressAsync("Enter");
            var row = page.Locator("tr", new PageLocatorOptions { Has = page.Locator($"button[aria-label='Icon for {_hashtagCategory}']") });
            await Expect(row.Locator(".hashtag-cell__text")).ToHaveTextAsync("#Homelab #SelfHosted");
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit hashtags for {_hashtagCategory}" }).ClickAsync();
            await Expect(row.GetByLabel("Networks").Nth(1)).ToHaveValueAsync("BlueskyOnly");
        }, element: page => page.Locator(".admin-table__scroll").First);

        AssertCaptureLanded("tags-hashtags", width, theme);
    }

    private static readonly SemaphoreSlim HashtagGate = new(1, 1);
    private static Guid _hashtagPostId;
    private static string _hashtagCategory = string.Empty;

    private async Task EnsureHashtagsAsync()
    {
        await HashtagGate.WaitAsync();
        try
        {
            if (_hashtagPostId != Guid.Empty)
            {
                return;
            }

            var runId = Guid.NewGuid().ToString("N")[..12];
            _hashtagCategory = $"Homelab capture {runId}";
            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            using var category = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories", JsonSerializer.Serialize(_hashtagCategory), token);
            var categoryId = category.RootElement.GetProperty("id").GetGuid();
            using var update = new HttpRequestMessage(HttpMethod.Put, $"{ApiUrl}/api/categories/{categoryId}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { name = _hashtagCategory, hashtags = new[] { new { name = "Homelab", networks = 0 }, new { name = "SelfHosted", networks = 2 } } }),
                    Encoding.UTF8, "application/json"),
            };
            update.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(update);
            response.EnsureSuccessStatusCode();
            using var post = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
                new { title = $"Hashtag capture {runId}", markdownContent = $"A post about a homelab {runId}.", subtitle = "What a small cluster can take", categoryId }, token);
            _hashtagPostId = post.RootElement.GetProperty("id").GetGuid();
        }
        finally
        {
            HashtagGate.Release();
        }
    }

    /// <summary>The Hidden replies page (brief 12c) with a reply block (with a note) and an account block, both made through the API as the admin.</summary>
    [TestCase(1440, 900, "light")]
    [TestCase(1440, 900, "dark")]
    [TestCase(390, 844, "light")]
    [TestCase(390, 844, "dark")]
    public async Task HiddenReplies(int width, int height, string theme)
    {
        await EnsureHiddenRepliesAsync();
        await CaptureAsync("hidden-replies", width, height, theme, "admin@example.com", async page =>
        {
            await page.GotoAsync($"{BaseUrl}/admin/discussion/blocks");
            await WaitForInteractiveAsync(page);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Hidden replies", Level = 1 })).ToBeVisibleAsync();
            await Expect(page.Locator("tr.admin-table__row").First).ToBeVisibleAsync();
        });

        AssertCaptureLanded("hidden-replies", width, theme);
    }

    private static readonly SemaphoreSlim HiddenGate = new(1, 1);
    private static bool _hiddenSeeded;

    private async Task EnsureHiddenRepliesAsync()
    {
        await HiddenGate.WaitAsync();
        try
        {
            if (_hiddenSeeded)
            {
                return;
            }

            var runId = Guid.NewGuid().ToString("N")[..8];
            using var client = MockIdpApi.CreateClient();
            var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
            await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/discussion/blocks", new Croft.Shared.Contracts.Social.CreateSocialBlockRequest(
                Croft.Shared.Contracts.Social.SocialPlatform.Mastodon, Croft.Shared.Contracts.Social.SocialBlockKind.Reply, $"shot-reply-{runId}",
                "@spam@social.example: Buy cheap watches at example.org today and every day after that, no questions asked, limited offer…",
                "Spam, reported to the server admins."), token);
            await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/discussion/blocks", new Croft.Shared.Contracts.Social.CreateSocialBlockRequest(
                Croft.Shared.Contracts.Social.SocialPlatform.Bluesky, Croft.Shared.Contracts.Social.SocialBlockKind.Account, $"did:plc:shot{runId}",
                "Troll Example (@troll.example)", null), token);
            _hiddenSeeded = true;
        }
        finally
        {
            HiddenGate.Release();
        }
    }

    private static readonly SemaphoreSlim DiscussionGate = new(1, 1);
    private static string _discussionSlug = string.Empty;
    private static Guid _discussionPostId;

    private async Task EnsureDiscussionAsync()
    {
        await DiscussionGate.WaitAsync();
        try
        {
            if (_discussionSlug.Length > 0)
            {
                return;
            }

            var runId = Guid.NewGuid().ToString("N")[..12];
            var (id, slug) = await DiscussionSeed.PublishPostAsync(ApiUrl, $"Discussion capture {runId}", $"A short post about paths {runId}.");
            await using var db = await OpenInstanceDatabaseAsync();
            var post = await DiscussionSeed.LinkAsync(db, id, slug, runId);
            await DiscussionSeed.AddRepliesAsync(db, InstanceNumber, post, DiscussionSeed.TheThread(runId));
            _discussionSlug = slug;
            _discussionPostId = id;
        }
        finally
        {
            DiscussionGate.Release();
        }
    }

    /// <summary>The outcome ruling R5-2-a allows: the capture completed and its PNG landed non-empty.</summary>
    private static void AssertCaptureLanded(string surface, int width, string theme)
    {
        var path = ScreenshotPath(surface, width, theme);
        Assert.That(File.Exists(path), Is.True, $"The {surface} capture did not land at {path}.");
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(0), $"The {surface} capture at {path} is empty.");
    }

    private static string ScreenshotPath(string surface, int width, string theme) =>
        Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots", $"{surface}-{width}-{theme}.png");

    /// <summary>The posts-grid row showing this title.</summary>
    private static ILocator RowFor(IPage page, string title) =>
        page.Locator(".posts-grid__row", new PageLocatorOptions { HasText = title });

    private static string Padded(string prefix)
    {
        var text = new StringBuilder(prefix);
        while (text.Length < 300)
        {
            text.Append("long capture words ");
        }

        return text.ToString(0, 300);
    }

    /// <summary>The captured post's body: several prose paragraphs, headings, a link, a code
    /// block and the list shapes (brief 03l item 1: a bulleted list wrapping a nested list and
    /// a numbered list) — enough text that the 88ch reading column is clearly filled at
    /// 1920/2560 and the 390px capture shows the list markers inside the column (the published
    /// post now also carries a banner).</summary>
    private static string PostBody() =>
        $"Capture fixture {RunId}: an opening paragraph with enough words to read comfortably " +
        "and to settle the reading measure before the wider breakpoints are reviewed.\n\n" +
        "## Capture section\n\n" +
        $"First body paragraph {RunId}, several sentences so the rendered lines fill the column " +
        "and the reviewer sees the measure and line height in the flow of real prose.\n\n" +
        $"Second body paragraph {RunId} with a [link to Home](/) inline, showing an anchor at " +
        "the reading measure rather than a stub line.\n\n" +
        $"Third body paragraph {RunId}, continuing the prose so the 88ch column is unmistakably " +
        "full at 1920 and 2560 pixels wide.\n\n" +
        "### A sub-section\n\n" +
        $"Fourth body paragraph {RunId} under the sub-section heading, adding column depth below " +
        "the fold so the capture shows the text block, not only the hero.\n\n" +
        "## Lists section\n\n" +
        $"List items {RunId} show the marker strip inside the text column on phones:\n\n" +
        "- Alpha item with enough words to wrap onto a second line on a narrow phone screen\n" +
        "  - Nested one\n" +
        "  - Nested two\n" +
        "- Beta item\n\n" +
        "1. First numbered step\n" +
        "2. Second numbered step\n\n" +
        "```csharp\n" +
        "var x = 1;\n" +
        "```\n";

    /// <summary>The draft's body: prose, a heading and a link, on a post whose optional fields
    /// are all missing (no category, banner, subtitle or tags).</summary>
    private static string DraftBody() =>
        $"Draft capture fixture {RunId} with enough words to read.\n\n" +
        "## Draft section\n\n" +
        $"Draft section body {RunId} with a [link to Home](/) inline.\n";

    /// <summary>The rhythm post's body: the vertical-rhythm shapes (two paragraphs, an h2, a tight
    /// list of three single-line items wrapping a nested list, an h3 and a code block), the same
    /// fixture PostRhythmTests measures.</summary>
    private static string RhythmBody() =>
        $"Rhythm fixture {RunId}, the first of two paragraphs whose spacing sets the reading rhythm.\n\n" +
        $"Second paragraph {RunId}, separated from the first by exactly one body em.\n\n" +
        "## Rhythm section\n\n" +
        "- Alpha item\n" +
        "- Beta item\n" +
        "- Gamma item\n" +
        "  - Nested one\n" +
        "  - Nested two\n\n" +
        "### Rhythm sub-section\n\n" +
        "```text\n" +
        "var rhythm = 1;\n" +
        "```";

    /// <summary>One fixture set per run (unique via <see cref="RunId"/>), created through the admin
    /// UI and shared by every capture: the long-text published post first, then the sparse draft.</summary>
    private async Task EnsureFixturesAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            await LoginAsync("admin@example.com", "password");

            await OpenNewPostEditorAsync();
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(LongTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Subtitle").FillAsync(LongSubtitle);
            await Page.GetByLabel("Subtitle").BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(PostBody());
            await Page.GetByLabel("Markdown").BlurAsync();
            await AddTagsAsync(TagList.Split(", ", StringSplitOptions.RemoveEmptyEntries));

            // Item 3: the published post now carries a banner. It is the run's own uploaded PNG
            // (the banner-upload pattern from AccessibilityTests — a clip of this page), so the
            // Post captures at 1920/2560 show a real banner image rather than the fallback banner.
            var bannerPng = await Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Clip = new Clip { X = 0, Y = 0, Width = 1200, Height = 400 }
            });
            await Page.GetByLabel("Upload banner").SetInputFilesAsync(new FilePayload
            {
                Name = $"banner-{RunId}.png",
                MimeType = "image/png",
                Buffer = bannerPng
            });
            await Expect(Page.GetByText($"banner-{RunId}.png").First).ToBeVisibleAsync();

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _postSlug = await SlugForPostAsync(PostIdFromEditorUrl());

            await using (var response = await Page.APIRequest.GetAsync($"{ApiUrl}/api/posts/{_postSlug}"))
            {
                Assert.That(response.Status, Is.EqualTo(200), $"The public API did not return the fixture post for slug {_postSlug}.");
                using var doc = JsonDocument.Parse(await response.TextAsync());
                Assert.That(doc.RootElement.GetProperty("tags").GetArrayLength(), Is.EqualTo(20),
                    "The captured post must carry all 20 tags (the long-text shape).");
            }

            await OpenNewPostEditorAsync();
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(DraftTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(DraftBody());
            await Page.GetByLabel("Markdown").BlurAsync();
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _draftId = PostIdFromEditorUrl();

            // The post-rhythm captures shoot their own post, created through the same admin-UI
            // flow and published so the captures need no sign-in. The draft's slug follows after.
            await OpenNewPostEditorAsync();
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(RhythmTitle);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(RhythmBody());
            await Page.GetByLabel("Markdown").BlurAsync();
            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
            _rhythmSlug = await SlugForPostAsync(PostIdFromEditorUrl());

            // The draft's slug comes from the Editor list's Review link — the public API cannot see
            // drafts anonymously — so the id-routed editor and the slug-routed review page address
            // the same draft.
            await SignOutAsync();
            await LoginAsync("editor@example.com", "password");
            await Page.GotoAsync($"{BaseUrl}/editor");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var review = RowFor(Page, DraftTitle)
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review" });
            await Expect(review).ToHaveAttributeAsync("href", new Regex("^/editor/posts/"));
            var href = await review.GetAttributeAsync("href");
            Assert.That(href, Does.StartWith("/editor/posts/"), "The Review link must lead to the draft review page.");
            _draftSlug = href!["/editor/posts/".Length..];

            await SignOutAsync();
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
