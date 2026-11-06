using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Someone with a few photos on a phone and a weak connection, sending them in through the contribution form.</summary>
[Collection(nameof(BrowserCollection))]
public class PhoneSendingTests(BrowserFixture app)
{
    private const string Send = "main form button[type=submit]";
    private const string Rows = ".chosen-photos li";
    private const string Title = "[name='Input.Title']";
    private const string Date = "[name='Input.Date']";
    private const string Description = "[name='Input.Description']";

    private static FilePayload Photo(string name) => new()
    {
        Name = name,
        MimeType = "image/jpeg",
        Buffer = File.ReadAllBytes(Path.Combine("Fixtures", "photo.jpg")),
    };

    private static string NewTitle() => $"Street party {Guid.NewGuid():N}";

    private Task<IPage> PhoneAsync() => app.NewPageAsync(390, 844);

    private async Task FillAsync(IPage page, string title)
    {
        await page.GotoAsync(app.ContributionPath);
        await page.FillAsync(Title, title);
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        await page.FillAsync("[name='Input.Name']", "Ada Contributor");
        await page.CheckAsync("[name='Input.Consent']");
    }

    private static async Task ChooseAsync(IPage page, int expectedRows, params FilePayload[] files)
    {
        await page.SetInputFilesAsync("#Files", files);
        await page.WaitForFunctionAsync(
            "([rows, count]) => document.querySelectorAll(rows).length === count",
            new object[] { Rows, expectedRows });
    }

    private static Task<bool> HasFocusAsync(IPage page, string selector) =>
        page.EvaluateAsync<bool>("selector => document.activeElement === document.querySelector(selector)", selector);

    private static int CountPosts(IPage page, List<string> posts)
    {
        page.Request += (_, request) =>
        {
            if (request.Method == "POST")
                posts.Add(request.Url);
        };
        return posts.Count;
    }

    [Fact]
    public async Task The_photos_come_first_and_send_takes_the_whole_width()
    {
        var page = await PhoneAsync();
        await page.GotoAsync(app.ContributionPath);

        var photos = await page.Locator("label[for=Files]").BoundingBoxAsync();
        var title = await page.Locator("label[for=Input_Title]").BoundingBoxAsync();
        Assert.True(photos!.Y < title!.Y, "The photos should come before the title.");

        var form = await page.Locator("main form").BoundingBoxAsync();
        var send = await page.Locator(Send).BoundingBoxAsync();
        Assert.Equal(form!.Width, send!.Width, 1);
        Assert.True(send.Height >= 44, $"Send is {send.Height} px high, hard to hit with a thumb.");

        // It was about 1,900 px long before, and that was a reason to come back to it.
        var length = await page.EvaluateAsync<int>("document.querySelector('main form').offsetHeight");
        Assert.True(length < 1500, $"The form is {length} px long on a phone.");

        // Once, on its own, with no introduction saying the same before it.
        Assert.Single(await page.Locator("main :text('A curator looks at everything')").AllAsync());
        Assert.DoesNotContain("We look at everything", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task Chosen_photos_can_be_seen_added_to_and_removed()
    {
        var page = await PhoneAsync();
        await page.GotoAsync(app.ContributionPath);

        await ChooseAsync(page, 2, Photo("one.jpg"), Photo("two.jpg"));
        // A second go adds to the first, the way it works when picking one photo at a time from a gallery.
        var fromTheDevice = Path.Combine("Fixtures", "photo.jpg");
        await page.SetInputFilesAsync("#Files", fromTheDevice);
        // The same photo twice is one photo.
        await page.SetInputFilesAsync("#Files", fromTheDevice);
        await ChooseAsync(page, 4, Photo("three.jpg"));

        Assert.Equal("Photos chosen: 4", await page.InnerTextAsync("#Files-chosen"));
        Assert.True(await page.Locator("button:text-is('Add more photos')").IsVisibleAsync());
        // Empty again, or a browser would stay silent when a photo that was removed is chosen once more.
        Assert.Equal(0, await page.EvaluateAsync<int>("document.querySelector('#Files').files.length"));
        // Real pictures, which also proves the page's security policy lets them through.
        await page.WaitForFunctionAsync(
            "rows => { const pictures = [...document.querySelectorAll(rows + ' img')]; return pictures.length === 4 && pictures.every(p => p.complete && p.naturalWidth > 0); }",
            Rows);
        await AccessibilityTests.AssertNoViolationsAsync(page, "the chosen photos");
        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"Scrolls sideways by {overflow} px.");

        await page.ClickAsync("button[aria-label='Remove two.jpg']");

        Assert.Equal("Photos chosen: 3", await page.InnerTextAsync("#Files-chosen"));
        Assert.Equal("Remove photo.jpg", await page.EvaluateAsync<string>("document.activeElement.getAttribute('aria-label')"));
        Assert.DoesNotContain("two.jpg", await page.InnerTextAsync(".chosen-photos"));

        // Removed, and chosen again: browsers tend to stay silent about that unless the field was emptied.
        await ChooseAsync(page, 4, Photo("two.jpg"));

        foreach (var name in new[] { "one.jpg", "photo.jpg", "three.jpg", "two.jpg" })
            await page.ClickAsync($"button[aria-label='Remove {name}']");

        Assert.Equal("No photos chosen", await page.InnerTextAsync("#Files-chosen"));
        Assert.True(await HasFocusAsync(page, "main form button[aria-describedby='Files-chosen']"));
        Assert.False(await page.Locator(".chosen-photos").IsVisibleAsync());
    }

    [Fact]
    public async Task What_cannot_be_sent_is_said_when_it_is_chosen()
    {
        var page = await PhoneAsync();
        var posts = new List<string>();
        CountPosts(page, posts);
        await page.GotoAsync(app.ContributionPath);

        await ChooseAsync(page, 1,
            Photo("fine.jpg"),
            new FilePayload { Name = "notes.jpg", MimeType = "image/jpeg", Buffer = "not a picture at all"u8.ToArray() },
            new FilePayload { Name = "IMG_0001.HEIC", MimeType = "image/heic", Buffer = [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0] },
            new FilePayload { Name = "empty.jpg", MimeType = "image/jpeg", Buffer = [] });

        var said = await page.InnerTextAsync("main form [role=alert]");
        Assert.Contains("notes.jpg is not a supported photo format", said);
        Assert.Contains("IMG_0001.HEIC is in the HEIC format", said);
        Assert.Contains("empty.jpg is empty", said);

        await page.SetInputFilesAsync("#Files", [.. Enumerable.Range(1, 12).Select(n => Photo($"more-{n}.jpg"))]);
        await page.WaitForSelectorAsync("main form [role=alert]:has-text('at most 10 photos')");
        Assert.Equal(10, await page.Locator(Rows).CountAsync());
        Assert.Single(await page.Locator("main form [role=alert] p").AllAsync());

        Assert.Empty(posts);
    }

    [Fact]
    public async Task A_wrong_date_is_found_before_the_photos_travel_and_they_stay_chosen()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        var posts = new List<string>();
        CountPosts(page, posts);
        await FillAsync(page, title);
        await ChooseAsync(page, 3, Photo("one.jpg"), Photo("two.jpg"), Photo("three.jpg"));
        await page.FillAsync(Date, "the winter when I was small");

        await page.ClickAsync(Send);
        await page.WaitForSelectorAsync("[data-mistakes]");

        Assert.Empty(posts);
        Assert.Equal(3, await page.Locator(Rows).CountAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.hasAttribute('data-mistakes')"));
        // Said on the field once, although the check while typing had said it too.
        await page.WaitForTimeoutAsync(600);
        Assert.Contains("could not be understood", await page.InnerTextAsync("#date-result"));
        Assert.Equal("", await page.InnerTextAsync("[data-valmsg-for='Input.Date']"));
        await AccessibilityTests.AssertNoViolationsAsync(page, "a mistake in a contribution");
        await page.ClickAsync("[data-mistakes] a");
        Assert.Equal("Input_Date", await page.EvaluateAsync<string>("document.activeElement.id"));

        // Corrected, it says so straight away and the mistake is no longer there.
        await page.FillAsync(Date, "1953");
        await page.WaitForSelectorAsync("#date-result:has-text('Understood as')");
        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal((1L, 3L), await app.CountContributionsAsync(title));
        Assert.Single(posts);
    }

    [Fact]
    public async Task What_only_the_archive_can_find_also_leaves_the_photos_chosen()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        await FillAsync(page, title);
        await ChooseAsync(page, 2, Photo("one.jpg"), Photo("two.jpg"));
        await page.FillAsync(Date, "the winter when I was small");
        // The check while typing has no connection, so the mistake only shows when the archive looks at it.
        await page.RouteAsync("**/date-check*", route => route.AbortAsync());

        await page.ClickAsync(Send);
        await page.WaitForSelectorAsync("[data-mistakes]");

        Assert.Contains("could not be understood", await page.InnerTextAsync("[data-mistakes]"));
        Assert.Contains("could not be understood", await page.InnerTextAsync("#date-result"));
        Assert.Equal("true", await page.GetAttributeAsync(Date, "aria-invalid"));
        Assert.Equal(2, await page.Locator(Rows).CountAsync());
        Assert.Equal((0L, 0L), await app.CountContributionsAsync(title));
        Assert.True(await page.Locator(Send).IsEnabledAsync());

        await page.FillAsync(Date, "1953");
        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal((1L, 2L), await app.CountContributionsAsync(title));
    }

    [Fact]
    public async Task Sending_shows_how_far_it_is_and_cannot_be_done_twice()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        var posts = new List<string>();
        CountPosts(page, posts);
        var dialogs = new List<string>();
        page.Dialog += async (_, dialog) =>
        {
            dialogs.Add(dialog.Type);
            await dialog.AcceptAsync();
        };
        await FillAsync(page, title);
        await page.FillAsync(Description, "With a text that was typed, so that leaving would normally be asked about.");
        var picture = Photo("big.jpg");
        await ChooseAsync(page, 1, new FilePayload { Name = picture.Name, MimeType = picture.MimeType, Buffer = [.. picture.Buffer, .. new byte[4 * 1024 * 1024]] });

        // What a slow mobile connection does: 1 MB takes about 3 seconds here.
        var network = await page.Context.NewCDPSessionAsync(page);
        await network.SendAsync("Network.enable");
        await network.SendAsync("Network.emulateNetworkConditions", new Dictionary<string, object>
        {
            ["offline"] = false,
            ["latency"] = 100,
            ["downloadThroughput"] = 350_000,
            ["uploadThroughput"] = 350_000,
        });

        await page.EvaluateAsync("""
            selector => {
                const button = document.querySelector(selector);
                button.click();
                button.click();
            }
            """, Send);
        await page.WaitForSelectorAsync("main progress", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });

        Assert.True(await page.Locator(Send).IsDisabledAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.querySelector('main form').inert"));
        await page.WaitForFunctionAsync("() => { const value = document.querySelector('main progress').value; return value > 0 && value < 1; }");
        await page.WaitForSelectorAsync("main section:has-text('Sending:')");
        // All of it in view by itself, on a phone where Send was at the very bottom. The page scrolls smoothly, so it may take a moment.
        await page.WaitForFunctionAsync("""
            () => {
                const stop = [...document.querySelectorAll('main button')].find(button => button.textContent === 'Stop sending').getBoundingClientRect();
                return stop.top >= 0 && stop.bottom <= innerHeight + 1;
            }
            """, null, new PageWaitForFunctionOptions { Timeout = 5000 });
        await AccessibilityTests.AssertNoViolationsAsync(page, "a contribution on its way");
        // A tap somewhere on the page, which used to count as being about to leave it.
        await page.Mouse.ClickAsync(5, 5);

        await page.WaitForURLAsync("**/thanks", new PageWaitForURLOptions { Timeout = 60000 });

        Assert.Equal((1L, 1L), await app.CountContributionsAsync(title));
        Assert.Single(posts);
        Assert.Empty(dialogs);
        Assert.Matches("Your reference: [0-9A-Z]{4}-[0-9A-Z]{4}", await page.InnerTextAsync("main"));

        // Going back does not lead to the sent form with everything still in it.
        await page.GoBackAsync();
        Assert.DoesNotContain("/thanks", page.Url);
        Assert.NotEqual(title, await page.Locator(Title).CountAsync() > 0 ? await page.InputValueAsync(Title) : "");
    }

    [Fact]
    public async Task When_the_answer_gets_lost_sending_again_does_not_make_two()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        await FillAsync(page, title);
        await ChooseAsync(page, 2, Photo("one.jpg"), Photo("two.jpg"));
        var tries = 0;
        await page.RouteAsync(url => url.EndsWith(app.ContributionPath), async route =>
        {
            if (route.Request.Method != "POST" || ++tries > 1)
            {
                await route.ContinueAsync();
                return;
            }
            // It arrives, and then the connection goes before the answer does.
            await route.FetchAsync();
            await route.AbortAsync();
        });

        await page.ClickAsync(Send);
        await page.WaitForSelectorAsync("main .alert-warning:has-text('The connection to the archive was lost')");

        Assert.False(await page.EvaluateAsync<bool>("document.querySelector('main form').inert"));
        Assert.Equal(2, await page.Locator(Rows).CountAsync());
        Assert.True(await HasFocusAsync(page, Send));
        Assert.Equal((1L, 2L), await app.CountContributionsAsync(title));

        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal((1L, 2L), await app.CountContributionsAsync(title));
        Assert.Contains("Your reference:", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task A_connection_that_goes_quiet_does_not_look_like_sending_forever()
    {
        var page = await PhoneAsync();
        await FillAsync(page, NewTitle());
        await page.Clock.InstallAsync();
        // No answer, and not a word about the upload either.
        await page.RouteAsync(url => url.EndsWith(app.ContributionPath), _ => Task.CompletedTask);

        await page.ClickAsync(Send);
        await page.WaitForSelectorAsync("main progress", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
        await page.Clock.FastForwardAsync(89_000);
        Assert.True(await page.Locator("main progress").IsVisibleAsync());

        await page.Clock.FastForwardAsync(2_000);

        await page.WaitForSelectorAsync("main .alert-warning:has-text('The connection to the archive was lost')");
        Assert.True(await page.Locator(Send).IsEnabledAsync());
    }

    [Fact]
    public async Task Sending_can_be_stopped_and_nothing_on_the_page_is_lost()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        await FillAsync(page, title);
        await ChooseAsync(page, 1, Photo("one.jpg"));
        var held = new TaskCompletionSource();
        await page.RouteAsync(url => url.EndsWith(app.ContributionPath), async route =>
        {
            if (!held.Task.IsCompleted)
                await held.Task;
            await route.ContinueAsync();
        });

        await page.ClickAsync(Send);
        await page.ClickAsync("main button:text-is('Stop sending')");
        await page.WaitForSelectorAsync("main .alert-warning:has-text('Sending stopped')");

        Assert.Equal(title, await page.InputValueAsync(Title));
        Assert.Equal(1, await page.Locator(Rows).CountAsync());
        await page.UnrouteAllAsync(new PageUnrouteAllOptions { Behavior = UnrouteBehavior.IgnoreErrors });
        held.SetResult();

        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");
        Assert.Equal((1L, 1L), await app.CountContributionsAsync(title));
    }

    [Fact]
    public async Task A_draft_comes_back_on_this_device_with_nothing_personal_in_it()
    {
        var page = await PhoneAsync();
        await page.GotoAsync(app.ContributionPath);
        await page.FillAsync(Title, "Flood in Mill Street");
        await page.FillAsync(Date, "1953");
        await page.FillAsync(Description, "My aunt is on the left.");
        await page.FillAsync("[name='Input.Name']", "Ada Contributor");
        await page.FillAsync("[name='Input.Email']", "ada@example.org");
        await page.CheckAsync("[name='Input.Rights'] >> nth=1");
        await page.FillAsync("[name='Input.RightsHolder']", "Hans Fotograf");
        await page.WaitForSelectorAsync(".draft-note:has-text('kept on this device')");

        var stored = await page.EvaluateAsync<string>("JSON.stringify(Object.entries(localStorage))");
        Assert.Contains("Flood in Mill Street", stored);
        Assert.Contains("My aunt is on the left.", stored);
        Assert.DoesNotContain("Ada", stored);
        Assert.DoesNotContain("ada@example.org", stored);
        Assert.DoesNotContain("Hans", stored);

        // Leaving asks first, because the name and the photos would still be lost.
        page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await page.ReloadAsync();

        Assert.Equal("Flood in Mill Street", await page.InputValueAsync(Title));
        Assert.Equal("1953", await page.InputValueAsync(Date));
        Assert.Equal("My aunt is on the left.", await page.InputValueAsync(Description));
        await page.WaitForSelectorAsync("#date-result:has-text('1953')");
        var note = await page.InnerTextAsync(".draft-note");
        Assert.Contains("If it is not yours, clear it", note);
        await AccessibilityTests.AssertNoViolationsAsync(page, "a draft that came back");

        await page.ClickAsync("button:text-is('Clear this draft')");

        Assert.Equal("", await page.InputValueAsync(Title));
        Assert.Equal("", await page.InputValueAsync(Date));
        Assert.Equal("", await page.InputValueAsync(Description));
        Assert.True(await HasFocusAsync(page, Title));
        Assert.False(await page.Locator(".draft-note").IsVisibleAsync());
        Assert.Equal("[]", await page.EvaluateAsync<string>("JSON.stringify(Object.keys(localStorage))"));

        await page.ReloadAsync();
        Assert.Equal("", await page.InputValueAsync(Title));
    }

    [Fact]
    public async Task A_draft_older_than_three_days_is_gone_and_so_are_those_of_other_links()
    {
        var page = await PhoneAsync();
        await page.GotoAsync(app.ContributionPath);
        var threeDaysAndABit = (3 * 24 * 60 * 60 * 1000) + 60_000;
        await page.EvaluateAsync("""
            ([path, age]) => {
                localStorage.setItem('commonage-contribution-draft:' + path, JSON.stringify({ title: 'Too old', savedAt: Date.now() - age }));
                localStorage.setItem('commonage-contribution-draft:/contribute/another-link', JSON.stringify({ title: 'Too old as well', savedAt: Date.now() - age }));
                localStorage.setItem('commonage-contribution-draft:/contribute/yet-another', JSON.stringify({ title: 'Recent', savedAt: Date.now() - 60000 }));
            }
            """, new object[] { app.ContributionPath, threeDaysAndABit });

        await page.ReloadAsync();

        Assert.Equal("", await page.InputValueAsync(Title));
        var left = await page.EvaluateAsync<string[]>("Object.keys(localStorage)");
        Assert.Equal("commonage-contribution-draft:/contribute/yet-another", Assert.Single(left));
    }

    [Fact]
    public async Task Sending_clears_the_draft_so_the_next_person_starts_empty()
    {
        var page = await PhoneAsync();
        await FillAsync(page, NewTitle());
        await page.FillAsync(Description, "Somebody's family.");
        await page.WaitForFunctionAsync("() => Object.keys(localStorage).length === 1");

        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal("[]", await page.EvaluateAsync<string>("JSON.stringify(Object.keys(localStorage))"));
        await page.ClickAsync("main a:text-is('Send in something else')");
        Assert.Equal("", await page.InputValueAsync(Title));
        Assert.Equal("", await page.InputValueAsync(Description));
    }

    [Fact]
    public async Task A_browser_without_storage_still_sends()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        await page.AddInitScriptAsync("Object.defineProperty(window, 'localStorage', { get() { throw new Error('Not allowed.'); } });");
        await FillAsync(page, title);
        await page.FillAsync(Description, "No storage here.");

        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal((1L, 0L), await app.CountContributionsAsync(title));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_photographers_name_is_only_asked_for_when_it_applies(bool javaScript)
    {
        var page = await app.NewPageAsync(390, 844, javaScript);
        await page.GotoAsync(app.ContributionPath);
        var field = page.Locator("[name='Input.RightsHolder']");

        // Without JavaScript nothing can show it later, so it is there from the start.
        Assert.Equal(!javaScript, await field.IsVisibleAsync());

        await page.CheckAsync("[name='Input.Rights'] >> nth=1");
        Assert.True(await field.IsVisibleAsync());

        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        Assert.Equal(!javaScript, await field.IsVisibleAsync());
    }

    [Fact]
    public async Task A_name_typed_and_then_hidden_is_not_sent()
    {
        var title = NewTitle();
        var page = await PhoneAsync();
        await FillAsync(page, title);
        string? sent = null;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST")
                sent = request.PostData;
        };
        await page.CheckAsync("[name='Input.Rights'] >> nth=1");
        await page.FillAsync("[name='Input.RightsHolder']", "Hans Fotograf");
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");

        await page.ClickAsync(Send);
        await page.WaitForURLAsync("**/thanks");

        Assert.NotNull(sent);
        Assert.DoesNotContain("Hans Fotograf", sent);
    }

    [Fact]
    public async Task The_form_reads_properly_in_right_to_left_at_a_narrow_width()
    {
        var page = await app.NewPageAsync(320, 640);
        await page.Context.AddCookiesAsync([new Cookie { Name = ".AspNetCore.Culture", Value = "c=ar|uic=ar", Url = app.BaseUrl }]);
        await page.GotoAsync(app.ContributionPath);

        await ChooseAsync(page, 2, Photo("a-rather-long-file-name-from-a-phone-camera-20261106-101112.jpg"), Photo("two.jpg"));

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"Scrolls sideways by {overflow} px.");
    }
}
