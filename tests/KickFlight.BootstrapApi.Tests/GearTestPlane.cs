using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KickFlight.BootstrapApi.Tests;

// gear/create spends a gear chip through economy's internal plane, so a test that rolls gears runs a host with that
// plane on and gives its player chips first.
internal static class GearTestPlane
{
    public static WebApplicationFactory<Program> WithInternalPlane(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otomo:Economy:ProgressEnabled"] = "true",
                ["Otomo:Economy:InternalBaseUrl"] = OtomoTestEnvironment.Economy.BaseUrl,
                ["Otomo:Economy:ServiceKey"] = "kf-test-gameserver-key"
            })));

    public static void GiveChips(string account, long chips = 10) =>
        OtomoTestEnvironment.Economy.Seed(account, [(EconomyCatalog.GearChipCurrency, chips)]);

    public static void GiveChipsToDevice(string uuid, long chips = 10) =>
        GiveChips(OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid))!, chips);
}
