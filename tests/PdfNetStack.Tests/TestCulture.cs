using System.Globalization;

namespace PdfNetStack.Tests;

/// <summary>
/// Runs the whole suite under the culture named by <c>PDFNETSTACK_TEST_CULTURE</c>, such as
/// tr-TR (dotted and dotless i) or de-DE (decimal comma), to catch parsing and formatting that
/// depend on the culture of the process. CI also runs the suite with
/// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>, as trimmed and Native AOT hosts often do.
/// </summary>
[TestClass]
public sealed class TestCulture
{
    private const string Variable = "PDFNETSTACK_TEST_CULTURE";

    [AssemblyInitialize]
    public static void Apply(TestContext context)
    {
        _ = context;
        string? name = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(name)) return;
        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    [TestMethod]
    public void TestsRunUnderTheRequestedCulture()
    {
        string? name = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(name)) return;
        Assert.AreEqual(name, CultureInfo.CurrentCulture.Name);
        Assert.AreEqual(name, Task.Run(() => CultureInfo.CurrentCulture.Name).Result);
    }
}
