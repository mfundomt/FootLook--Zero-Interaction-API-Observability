using FootLook.Core.Options;
using FootLook.Core.Security;

namespace FootLook.Tests;

public class JsonFileUserStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"footlook-userstore-{Guid.NewGuid():N}");
    private string PathToFile => Path.Combine(_dir, "nested", "users.json");

    private JsonFileUserStore Create() => new(new FootLookOptions { UserStorePath = PathToFile });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void First_user_is_admin_and_later_users_are_not()
    {
        var store = Create();

        var first = store.TryCreate("first@example.com", "First", "hash1");
        var second = store.TryCreate("second@example.com", "Second", "hash2");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first!.IsAdmin);
        Assert.False(second!.IsAdmin);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Duplicate_email_returns_null_regardless_of_case_or_whitespace()
    {
        var store = Create();
        Assert.NotNull(store.TryCreate("Dev@Example.com", "Dev", "hash"));

        Assert.Null(store.TryCreate("dev@example.com", "Again", "hash"));
        Assert.Null(store.TryCreate("DEV@EXAMPLE.COM", "Again", "hash"));
        Assert.Null(store.TryCreate("  dev@example.com ", "Again", "hash"));
    }

    [Fact]
    public void Email_is_stored_normalised_and_lookup_is_case_insensitive()
    {
        var store = Create();
        var created = store.TryCreate("Dev@Example.COM", "Dev", "hash")!;

        Assert.Equal("dev@example.com", created.Email);
        Assert.Equal(created.Id, store.FindByEmail("dev@example.com")!.Id);
        Assert.Equal(created.Id, store.FindByEmail("DEV@example.com")!.Id);
        Assert.Equal(created.Id, store.FindByEmail("  Dev@Example.com  ")!.Id);
        Assert.Null(store.FindByEmail("other@example.com"));
    }

    [Fact]
    public void FindById_returns_the_account_or_null()
    {
        var store = Create();
        var created = store.TryCreate("dev@example.com", "Dev", "hash")!;

        Assert.Equal(created, store.FindById(created.Id));
        Assert.Null(store.FindById("nope"));
    }

    [Fact]
    public void Accounts_persist_across_instances_including_admin_flag_and_hash()
    {
        var first = Create();
        var admin = first.TryCreate("admin@example.com", "Admin", "hash-admin")!;
        var regular = first.TryCreate("user@example.com", "User", "hash-user")!;

        var reloaded = Create();

        var a = reloaded.FindByEmail("admin@example.com");
        var u = reloaded.FindById(regular.Id);
        Assert.NotNull(a);
        Assert.NotNull(u);
        Assert.Equal(admin.Id, a!.Id);
        Assert.True(a.IsAdmin);
        Assert.Equal("hash-admin", a.PasswordHash);
        Assert.Equal("User", u!.DisplayName);
        Assert.False(u.IsAdmin);
        Assert.False(File.Exists(PathToFile + ".tmp"));
    }

    [Fact]
    public void A_reloaded_store_does_not_hand_out_a_second_admin()
    {
        Create().TryCreate("admin@example.com", "Admin", "hash");

        var third = Create().TryCreate("later@example.com", "Later", "hash");

        Assert.False(third!.IsAdmin);
        Assert.Null(Create().TryCreate("ADMIN@example.com", "Dup", "hash"));
    }

    [Fact]
    public void Missing_file_means_an_empty_store()
    {
        var store = Create();

        Assert.Null(store.FindByEmail("anyone@example.com"));
        Assert.False(File.Exists(PathToFile));
    }
}
