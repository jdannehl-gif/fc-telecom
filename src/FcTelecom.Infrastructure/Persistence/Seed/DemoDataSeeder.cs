using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Notifications;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Platform;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FcTelecom.Infrastructure.Persistence.Seed;

/// <summary>
/// Seeds reference data always, and demo data on request.
/// </summary>
/// <remarks>
/// <para>
/// The demo estate is deliberately imperfect. It contains two locations whose "diverse"
/// backup shares a last-mile provider, several services with no circuit ID, a legacy POTS
/// group with no contract terms on file, an unmonitored SIP trunk, and a circuit whose
/// availability sits below its SLA. Seed data where everything is complete and healthy
/// demonstrates nothing: the reports that matter — diversity risk, data completeness,
/// SLA credits — all render as empty states, and nobody discovers whether they work.
/// </para>
/// <para>
/// Carrier names are real because circuit ID formats are carrier-specific and the import
/// and search paths should be exercised against realistic shapes. Every account number,
/// address, circuit ID, contact, and price is fictional.
/// </para>
/// </remarks>
public sealed class DemoDataSeeder(
    ApplicationDbContext db,
    IFieldEncryptor encryptor,
    IClock clock,
    ILogger<DemoDataSeeder> logger)
{
    /// <summary>
    /// Seeds role permissions and notification rules. Safe to run on every start,
    /// including in production.
    /// </summary>
    public async Task SeedReferenceDataAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolePermissionsAsync(cancellationToken).ConfigureAwait(false);
        await SeedNotificationRulesAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds the demo estate. No-ops if any locations already exist.
    /// </summary>
    /// <remarks>
    /// Guarded by an existence check rather than by configuration alone, so a
    /// misconfigured production environment cannot inject twelve fictional clinics into
    /// a real inventory.
    /// </remarks>
    public async Task SeedDemoDataAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Locations.IgnoreQueryFilters().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Locations already exist; skipping demo data seed.");
            return;
        }

        logger.LogInformation("Seeding demo data.");

        var regions = new[]
        {
            new Region { Name = "Midwest", Code = "MW" },
            new Region { Name = "Southeast", Code = "SE" },
            new Region { Name = "Northeast", Code = "NE" },
            new Region { Name = "West", Code = "W" },
        };
        db.Regions.AddRange(regions);

        var businessUnits = new[]
        {
            new BusinessUnit { Name = "Clinical Services", Code = "CLIN" },
            new BusinessUnit { Name = "Distribution", Code = "DIST" },
            new BusinessUnit { Name = "Corporate", Code = "CORP" },
        };
        db.BusinessUnits.AddRange(businessUnits);

        var costCenters = new[]
        {
            new CostCenter { Code = "4400-CLIN", Name = "Clinical Network Services", GlAccount = "6120" },
            new CostCenter { Code = "4410-DIST", Name = "Distribution Network Services", GlAccount = "6120" },
            new CostCenter { Code = "4100-CORP", Name = "Corporate IT", GlAccount = "6110" },
        };
        db.CostCenters.AddRange(costCenters);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Vendor lumen = Carrier("Level 3 Financing, Inc. d/b/a Lumen Technologies", "Lumen",
            "https://control.lumen.com", "(877) 453-8353", "24×7", "1Password → Carriers → Lumen");
        Vendor spectrum = Carrier("Charter Communications Operating, LLC", "Spectrum",
            "https://enterprise.spectrum.com", "(800) 314-7195", "24×7", "1Password → Carriers → Spectrum");
        Vendor att = Carrier("AT&T Corp.", "AT&T",
            "https://businesscenter.att.com", "(888) 613-6330", "24×7", "1Password → Carriers → AT&T");
        Vendor comcast = Carrier("Comcast Cable Communications Management, LLC", "Comcast Business",
            "https://business.comcast.com", "(800) 741-4141", "24×7", "1Password → Carriers → Comcast");
        Vendor verizon = Carrier("Verizon Business Network Services LLC", "Verizon",
            "https://enterprisecenter.verizon.com", "(800) 569-8799", "24×7", "1Password → Carriers → Verizon");

        var intrado = new Vendor
        {
            LegalName = "Intrado Life & Safety, Inc.",
            DisplayName = "Intrado",
            Kind = VendorKind.Carrier,
            MainSupportPhone = "(800) 911-1234",
            SupportHours = "24×7",
            Notes = "SIP trunking and E911. Verify registered addresses after any office move.",
        };

        // Last-mile only. This vendor is the point of the whole diversity model: it never
        // appears on an invoice, and it is the reason two "diverse" circuits at Northgate
        // and Riverside are not diverse at all.
        var everstream = new Vendor
        {
            LegalName = "Everstream Solutions LLC",
            DisplayName = "Everstream",
            Kind = VendorKind.LastMileProvider,
            Notes = "Regional dark-fibre and last-mile provider. Underlies circuits sold by " +
                    "several different carriers — check before treating a backup as diverse.",
        };

        var tierPoint = new Vendor
        {
            LegalName = "TierPoint, LLC",
            DisplayName = "TierPoint",
            Kind = VendorKind.Reseller,
            MainSupportPhone = "(877) 859-8324",
            SupportHours = "24×7",
        };

        var granite = new Vendor
        {
            LegalName = "Granite Telecommunications, LLC",
            DisplayName = "Granite",
            Kind = VendorKind.Reseller,
            MainSupportPhone = "(866) 847-5500",
            SupportHours = "M–F 08:00–20:00 ET",
            Notes = "Aggregates POTS lines across incumbent carriers. One invoice, many " +
                    "underlying providers — expect reconciliation to be fiddly.",
        };

        db.Vendors.AddRange(lumen, spectrum, att, comcast, verizon, intrado, everstream, tierPoint, granite);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        db.VendorTicketProcedures.AddRange(
            new VendorTicketProcedure
            {
                VendorId = lumen.Id,
                ScenarioName = "Circuit down — fibre DIA",
                PhoneNumber = "(877) 453-8353",
                PortalUrl = "https://control.lumen.com",
                HoursOfOperation = "24×7",
                RequiredInformation =
                    "Circuit ID (ECCKT format), billing account number, site contact name and " +
                    "mobile, and confirmation that CPE has power and link lights. They will not " +
                    "dispatch without a site contact who can provide access.",
                Procedure =
                    "1. Confirm the CPE has power and the WAN link light state.\n" +
                    "2. Open via portal if reachable; phone if the portal is unavailable.\n" +
                    "3. Ask explicitly for a ticket number before ending the call.\n" +
                    "4. Request an intrusive test window if the fault is intermittent.",
                ExpectedResponseTime = "P1: 30 minutes to acknowledge, 4-hour MTTR target.",
            },
            new VendorTicketProcedure
            {
                VendorId = spectrum.Id,
                ScenarioName = "Circuit down — coax",
                PhoneNumber = "(800) 314-7195",
                HoursOfOperation = "24×7",
                RequiredInformation =
                    "Account number and service address. They index by address more reliably " +
                    "than by circuit ID — have both ready.",
                ExpectedResponseTime = "Next business day for standard; same day for priority accounts.",
            },
            new VendorTicketProcedure
            {
                VendorId = granite.Id,
                ScenarioName = "POTS line dead — alarm or elevator",
                PhoneNumber = "(866) 847-5500",
                HoursOfOperation = "M–F 08:00–20:00 ET",
                RequiredInformation =
                    "Working telephone number (WTN) and the site address. Flag alarm and " +
                    "elevator lines as life-safety on the ticket — it changes the priority.",
                ExpectedResponseTime = "24–72 hours. Slower than you will want for an elevator line.",
            });

        var contacts = new[]
        {
            InternalContact("Marisol Reyes", "IT Director", "mreyes@example.org", "(312) 555-0143"),
            InternalContact("Aisha Okafor", "Clinic Operations Manager", "aokafor@example.org", "(312) 555-0177"),
            InternalContact("Dan Whitfield", "Warehouse Supervisor", "dwhitfield@example.org", "(614) 555-0122"),
            InternalContact("Priya Raman", "Network Engineer", "praman@example.org", "(312) 555-0166"),
            VendorContact(lumen.Id, "Kevin Tarrant", "Named Account Manager", ContactKind.VendorSales,
                "k.tarrant@example-lumen.com", "(720) 555-0188", escalation: null),
            VendorContact(lumen.Id, "Lumen NOC Escalation", "Tier 2 Escalation", ContactKind.VendorNocEscalation,
                null, "(877) 453-8353", escalation: 1),
            VendorContact(spectrum.Id, "Dana Whitlock", "Account Executive", ContactKind.VendorSales,
                "d.whitlock@example-spectrum.com", "(704) 555-0131", escalation: null),
            VendorContact(att.Id, "AT&T Billing Inquiries", "Billing", ContactKind.VendorBilling,
                "billing@example-att.com", "(888) 613-6330", escalation: null),
        };
        db.Contacts.AddRange(contacts);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Contact reyes = contacts[0];
        Contact okafor = contacts[1];
        Contact whitfield = contacts[2];

        // ── Locations ────────────────────────────────────────────────────────────────
        var northgate = Site("ST-0142", "Northgate Clinic", LocationType.Clinic, "4820 N Broadway",
            "Chicago", "IL", "60640", "America/Chicago", regions[0], businessUnits[0], costCenters[0],
            Criticality.Critical, 15, reyes, 41.968m, -87.660m);

        var riverside = Site("ST-0187", "Riverside Clinic", LocationType.Clinic, "1140 W Jackson Blvd",
            "Chicago", "IL", "60607", "America/Chicago", regions[0], businessUnits[0], costCenters[0],
            Criticality.Critical, 15, reyes, 41.877m, -87.655m);

        var columbus = Site("DC-0301", "Columbus Distribution Center", LocationType.Warehouse,
            "3900 Groveport Rd", "Columbus", "OH", "43207", "America/New_York", regions[0], businessUnits[1],
            costCenters[1], Criticality.High, 60, whitfield, 39.876m, -82.940m);

        var hq = Site("HQ-0001", "Corporate Headquarters", LocationType.Office, "200 W Madison St, Suite 3400",
            "Chicago", "IL", "60606", "America/Chicago", regions[0], businessUnits[2], costCenters[2],
            Criticality.Critical, 30, reyes, 41.882m, -87.635m);

        var atlanta = Site("ST-0455", "Peachtree Clinic", LocationType.Clinic, "1720 Peachtree St NW",
            "Atlanta", "GA", "30309", "America/New_York", regions[1], businessUnits[0], costCenters[0],
            Criticality.High, 30, reyes, 33.797m, -84.388m);

        var charlotte = Site("ST-0462", "Southpark Clinic", LocationType.Clinic, "6000 Fairview Rd",
            "Charlotte", "NC", "28210", "America/New_York", regions[1], businessUnits[0], costCenters[0],
            Criticality.Standard, 120, reyes, 35.150m, -80.830m);

        var boston = Site("ST-0510", "Back Bay Clinic", LocationType.Clinic, "800 Boylston St",
            "Boston", "MA", "02199", "America/New_York", regions[2], businessUnits[0], costCenters[0],
            Criticality.High, 30, reyes, 42.347m, -71.082m);

        var newark = Site("DC-0512", "Newark Distribution Center", LocationType.Warehouse, "500 Doremus Ave",
            "Newark", "NJ", "07105", "America/New_York", regions[2], businessUnits[1], costCenters[1],
            Criticality.High, 60, whitfield, 40.712m, -74.121m);

        var denver = Site("ST-0620", "Cherry Creek Clinic", LocationType.Clinic, "3000 E 1st Ave",
            "Denver", "CO", "80206", "America/Denver", regions[3], businessUnits[0], costCenters[0],
            Criticality.Standard, 120, reyes, 39.717m, -104.955m);

        var phoenix = Site("ST-0644", "Camelback Clinic", LocationType.Clinic, "2400 E Camelback Rd",
            "Phoenix", "AZ", "85016", "America/Phoenix", regions[3], businessUnits[0], costCenters[0],
            Criticality.Standard, 120, reyes, 33.509m, -112.030m);

        var seattle = Site("ST-0701", "Ballard Clinic", LocationType.Clinic, "1455 NW Leary Way",
            "Seattle", "WA", "98107", "America/Los_Angeles", regions[3], businessUnits[0], costCenters[0],
            Criticality.Standard, 120, reyes, 47.663m, -122.375m);

        var remoteOffice = Site("RO-0880", "Springfield Satellite Office", LocationType.Office,
            "215 S 6th St", "Springfield", "IL", "62701", "America/Chicago", regions[0], businessUnits[2],
            costCenters[2], Criticality.Low, 480, reyes, 39.799m, -89.647m);

        Location[] locations =
        [
            northgate, riverside, columbus, hq, atlanta, charlotte,
            boston, newark, denver, phoenix, seattle, remoteOffice,
        ];

        db.Locations.AddRange(locations);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        db.LocationContacts.AddRange(
            new LocationContact { LocationId = northgate.Id, ContactId = okafor.Id, RoleAtLocation = "Site manager", IsPrimary = true },
            new LocationContact { LocationId = columbus.Id, ContactId = whitfield.Id, RoleAtLocation = "Site manager", IsPrimary = true });

        // External identifiers, seeded for most locations but deliberately not all.
        //
        // Northgate, the remote office, and the Springfield satellite have no Agris code —
        // a leased clinic suite, a satellite office, and a facility that predates the
        // current master. That is the normal case, not an error, and it is exactly why
        // LocationCode is the permanent enterprise key and the Agris value hangs off it
        // rather than being it.
        foreach ((Location site, string agrisCode) in new[]
        {
            (riverside, "AG-1187"),
            (columbus, "AG-3301"),
            (hq, "AG-0001"),
            (atlanta, "AG-4455"),
            (charlotte, "AG-4462"),
            (boston, "AG-5510"),
            (newark, "AG-5512"),
            (denver, "AG-6620"),
            (phoenix, "AG-6644"),
        })
        {
            db.LocationExternalIdentifiers.Add(new LocationExternalIdentifier
            {
                LocationId = site.Id,
                SystemKey = ExternalLocationSystems.Agris,
                Value = agrisCode,
                Notes = "Seeded from the initial business-location list.",
            });
        }

        VendorAccount lumenAccount = Account(lumen.Id, "8-2K4H91", "402-11940", "Enterprise fibre — Midwest");
        VendorAccount spectrumAccount = Account(spectrum.Id, "8245-1190-0034", null, "Business internet — national");
        VendorAccount attAccount = Account(att.Id, "831-000-4471-005", "BAN 831-000-4471", "MPLS and voice");
        VendorAccount comcastAccount = Account(comcast.Id, "8497 10 220 0044182", null, "Business internet — Southeast");
        VendorAccount verizonAccount = Account(verizon.Id, "VZ-4410-88213", null, "Wireless backup — national");
        VendorAccount intradoAccount = Account(intrado.Id, "INT-77120", null, "SIP trunking");
        VendorAccount graniteAccount = Account(granite.Id, "GRN-220145", null, "Aggregated POTS");

        db.VendorAccounts.AddRange(
            lumenAccount, spectrumAccount, attAccount, comcastAccount,
            verizonAccount, intradoAccount, graniteAccount);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await SeedServicesAsync(
            new SeedContext(
                locations, lumen, spectrum, att, comcast, verizon, intrado, everstream, granite,
                lumenAccount, spectrumAccount, attAccount, comcastAccount, verizonAccount,
                intradoAccount, graniteAccount),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Demo data seeding complete.");
    }

    private sealed record SeedContext(
        Location[] Locations, Vendor Lumen, Vendor Spectrum, Vendor Att, Vendor Comcast,
        Vendor Verizon, Vendor Intrado, Vendor Everstream, Vendor Granite,
        VendorAccount LumenAccount, VendorAccount SpectrumAccount, VendorAccount AttAccount,
        VendorAccount ComcastAccount, VendorAccount VerizonAccount, VendorAccount IntradoAccount,
        VendorAccount GraniteAccount);

    private async Task SeedServicesAsync(SeedContext context, CancellationToken cancellationToken)
    {
        Location northgate = context.Locations[0];
        Location riverside = context.Locations[1];
        Location columbus = context.Locations[2];
        Location hq = context.Locations[3];
        Location atlanta = context.Locations[4];
        Location charlotte = context.Locations[5];
        Location boston = context.Locations[6];
        Location newark = context.Locations[7];
        Location denver = context.Locations[8];
        Location phoenix = context.Locations[9];
        Location seattle = context.Locations[10];
        Location springfield = context.Locations[11];

        var services = new List<TelecomService>();

        // ── Northgate: the flagship demo. Primary + backup that LOOK diverse and are not.
        TelecomService northgatePrimary = Circuit(
            northgate, context.Lumen, context.LumenAccount, ServiceType.Internet, ServiceRole.Primary,
            "ORD/KFGS/123456/LMKT", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "MDF, Rack 3, Panel A, Port 12", "Adtran", "834-5", "A4X99120", true, "ether1 (FGT-60F-NGATE)",
            SupportPriority.P1, downloadKbps: 1_000_000, uploadKbps: 1_000_000, cirKbps: 1_000_000,
            slaAvailability: 99.99m, slaLatencyMs: 45, lastMile: context.Everstream);

        TelecomService northgateBackup = Circuit(
            northgate, context.Spectrum, context.SpectrumAccount, ServiceType.Internet, ServiceRole.Secondary,
            "60.LXFN.845512.CHI", TransportMedia.Coax, HandoffType.Rj45,
            "IDF-2, Panel B, Port 4", "Technicolor", "CGA4131", "SPX7742199", true, "ether2 (FGT-60F-NGATE)",
            SupportPriority.P2, downloadKbps: 600_000, uploadKbps: 35_000, cirKbps: 0,
            slaAvailability: 99.9m, slaLatencyMs: null, lastMile: context.Everstream);

        TelecomService northgateSip = VoiceCircuit(
            northgate, context.Intrado, context.IntradoAccount, ServiceType.SipTrunk,
            "INT-CHI-77120-01", channels: 24, btn: "(312) 555-0140");

        // Deliberately incomplete: no circuit ID, no contract. Feeds the data-completeness
        // report, and mirrors how legacy alarm lines actually appear in real inventories.
        TelecomService northgateAlarm = new()
        {
            ServiceType = ServiceType.AlarmLine,
            LocationId = northgate.Id,
            CarrierVendorId = context.Att.Id,
            VendorAccountId = context.GraniteAccount.Id,
            Status = ServiceStatus.Active,
            ServiceRole = ServiceRole.Standalone,
            Media = TransportMedia.Copper,
            HandoffType = HandoffType.Unknown,
            SupportPriority = SupportPriority.P3,
            TechnicalNotes = "Fire panel dial-out. Inherited at acquisition; no paperwork located.",
        };

        services.AddRange([northgatePrimary, northgateBackup, northgateSip, northgateAlarm]);

        // ── Riverside: the second fake-diversity case, this one via shared conduit.
        TelecomService riversidePrimary = Circuit(
            riverside, context.Lumen, context.LumenAccount, ServiceType.Internet, ServiceRole.Primary,
            "ORD/KFGS/224781/LMKT", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "Basement MPOE, Rack 1", "Adtran", "834-5", "A4X99455", true, "ether1 (FGT-60F-RVSD)",
            SupportPriority.P1, 500_000, 500_000, 500_000, 99.99m, 45, context.Everstream);

        TelecomService riversideBackup = Circuit(
            riverside, context.Comcast, context.ComcastAccount, ServiceType.Internet, ServiceRole.Secondary,
            "44 8497 220 0044182", TransportMedia.Coax, HandoffType.Rj45,
            "Basement MPOE, Rack 1", "Comcast", "CBR-T", "CMB4419023", true, "ether2 (FGT-60F-RVSD)",
            SupportPriority.P2, 800_000, 40_000, 0, 99.9m, null, lastMile: null);

        TelecomService riversideCellular = Circuit(
            riverside, context.Verizon, context.VerizonAccount, ServiceType.CellularBackup,
            ServiceRole.Tertiary, "VZW-CHI-4410-8821", TransportMedia.Cellular, HandoffType.Wireless,
            "Rooftop antenna, mast 2", "Cradlepoint", "E300", "CP88120043", false, "wwan0",
            SupportPriority.P3, 150_000, 25_000, 0, null, null, lastMile: null);

        services.AddRange([riversidePrimary, riversideBackup, riversideCellular]);

        // ── HQ: genuinely diverse, and assessed as such.
        TelecomService hqPrimary = Circuit(
            hq, context.Lumen, context.LumenAccount, ServiceType.Internet, ServiceRole.Primary,
            "ORD/KFGS/100001/LMKT", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "34th floor IDF, Rack A2", "Cisco", "ASR-920", "CSC7710042", false, "Gi0/0/1",
            SupportPriority.P1, 10_000_000, 10_000_000, 10_000_000, 99.999m, 30, lastMile: null);

        TelecomService hqSecondary = Circuit(
            hq, context.Att, context.AttAccount, ServiceType.Internet, ServiceRole.Secondary,
            "IPFR-88210-ORD", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "Sub-basement MPOE (separate entrance), Rack C1", "Cisco", "ASR-920", "CSC7710099", false, "Gi0/0/2",
            SupportPriority.P1, 5_000_000, 5_000_000, 5_000_000, 99.99m, 35, lastMile: null);

        TelecomService hqMpls = Circuit(
            hq, context.Att, context.AttAccount, ServiceType.MplsVpn, ServiceRole.Standalone,
            "VPLS-4471-HUB", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "34th floor IDF, Rack A3", "Cisco", "ISR-4451", "CSC7710123", false, "Gi0/0/3",
            SupportPriority.P1, 1_000_000, 1_000_000, 1_000_000, 99.99m, 40, lastMile: null);

        TelecomService hqSip = VoiceCircuit(hq, context.Intrado, context.IntradoAccount,
            ServiceType.SipTrunk, "INT-CHI-77120-HQ", 240, "(312) 555-0100");

        services.AddRange([hqPrimary, hqSecondary, hqMpls, hqSip]);

        // ── Columbus DC: single circuit, no backup at all. The NoBackup verdict.
        TelecomService columbusPrimary = Circuit(
            columbus, context.Spectrum, context.SpectrumAccount, ServiceType.Internet, ServiceRole.Primary,
            "60.LXFN.771203.CMH", TransportMedia.Fiber, HandoffType.Rj45,
            "Dock office, wall-mount panel", "Adtran", "834-5", "A4X99781", true, "ether1 (FGT-100F-CMH)",
            SupportPriority.P1, 1_000_000, 1_000_000, 1_000_000, 99.9m, 45, lastMile: null);

        TelecomService columbusPri = VoiceCircuit(columbus, context.Att, context.AttAccount,
            ServiceType.Pri, "PRI-CMH-4471-01", 23, "(614) 555-0120");

        services.AddRange([columbusPrimary, columbusPri]);

        // ── Remaining sites: one primary, most with a backup.
        services.Add(Circuit(atlanta, context.Comcast, context.ComcastAccount, ServiceType.Internet,
            ServiceRole.Primary, "44 8497 310 0088211", TransportMedia.Coax, HandoffType.Rj45,
            "Suite 210 telecom closet", "Comcast", "CBR-T", "CMB4419881", true, "ether1",
            SupportPriority.P2, 1_000_000, 35_000, 0, 99.9m, null, lastMile: null));

        services.Add(Circuit(atlanta, context.Verizon, context.VerizonAccount, ServiceType.CellularBackup,
            ServiceRole.Secondary, "VZW-ATL-4410-2210", TransportMedia.Cellular, HandoffType.Wireless,
            "Suite 210 telecom closet", "Cradlepoint", "E300", "CP88120099", false, "wwan0",
            SupportPriority.P3, 150_000, 25_000, 0, null, null, lastMile: null));

        // No circuit ID recorded — feeds the completeness report.
        TelecomService charlottePrimary = Circuit(charlotte, context.Spectrum, context.SpectrumAccount,
            ServiceType.Internet, ServiceRole.Primary, null, TransportMedia.Coax, HandoffType.Rj45,
            null, null, null, null, true, null, SupportPriority.P2,
            600_000, 35_000, 0, null, null, lastMile: null);
        services.Add(charlottePrimary);

        services.Add(Circuit(boston, context.Lumen, context.LumenAccount, ServiceType.Internet,
            ServiceRole.Primary, "BOS/KFGS/551002/LMKT", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "Floor 4 IDF, Panel A", "Adtran", "834-5", "A4X99902", true, "ether1",
            SupportPriority.P1, 1_000_000, 1_000_000, 1_000_000, 99.99m, 45, lastMile: null));

        services.Add(Circuit(boston, context.Comcast, context.ComcastAccount, ServiceType.Internet,
            ServiceRole.Secondary, "44 8497 511 0022110", TransportMedia.Coax, HandoffType.Rj45,
            "Floor 4 IDF, Panel B", "Comcast", "CBR-T", "CMB4419223", true, "ether2",
            SupportPriority.P2, 800_000, 40_000, 0, 99.9m, null, lastMile: null));

        services.Add(Circuit(newark, context.Verizon, context.VerizonAccount, ServiceType.Internet,
            ServiceRole.Primary, "VZ-NWK-88213-DIA", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "Warehouse office MPOE", "Cisco", "ISR-1111", "CSC7710881", false, "GigabitEthernet0/0/0",
            SupportPriority.P1, 1_000_000, 1_000_000, 1_000_000, 99.99m, 40, lastMile: null));

        services.Add(Circuit(denver, context.Comcast, context.ComcastAccount, ServiceType.Internet,
            ServiceRole.Primary, "44 8497 620 0011903", TransportMedia.Coax, HandoffType.Rj45,
            "Suite 120 closet", "Comcast", "CBR-T", "CMB4419620", true, "ether1",
            SupportPriority.P2, 800_000, 35_000, 0, 99.9m, null, lastMile: null));

        // A legacy T1 at an absurd cost per Mbps. Feeds the outlier report.
        services.Add(Circuit(phoenix, context.Att, context.AttAccount, ServiceType.Internet,
            ServiceRole.Primary, "T1-PHX-4471-0088", TransportMedia.Copper, HandoffType.T1Rj48,
            "Suite 300 closet, smartjack on wall", "Adtran", "Total Access 900", "ADT9900112", false, "Serial0/0",
            SupportPriority.P2, 1_500, 1_500, 1_500, 99.9m, 60, lastMile: null));

        services.Add(Circuit(seattle, context.Lumen, context.LumenAccount, ServiceType.Internet,
            ServiceRole.Primary, "SEA/KFGS/701004/LMKT", TransportMedia.Fiber, HandoffType.SingleModeFiberLc,
            "Building A telecom room", "Adtran", "834-5", "A4X99777", true, "ether1",
            SupportPriority.P1, 1_000_000, 1_000_000, 1_000_000, 99.99m, 45, lastMile: null));

        services.Add(Circuit(springfield, context.Spectrum, context.SpectrumAccount, ServiceType.Internet,
            ServiceRole.Primary, "60.LXFN.880011.SPI", TransportMedia.Coax, HandoffType.Rj45,
            "Reception closet", null, null, null, true, "ether1", SupportPriority.P3,
            400_000, 20_000, 0, null, null, lastMile: null));

        // Disconnected six months ago but still carrying a cost record — the
        // "still being billed" detector has something to find.
        var decommissioned = new TelecomService
        {
            ServiceType = ServiceType.Internet,
            LocationId = springfield.Id,
            CarrierVendorId = context.Att.Id,
            VendorAccountId = context.AttAccount.Id,
            CircuitId = "T1-SPI-4471-0012",
            Status = ServiceStatus.Disconnected,
            ServiceRole = ServiceRole.Standalone,
            Media = TransportMedia.Copper,
            HandoffType = HandoffType.T1Rj48,
            SupportPriority = SupportPriority.P3,
            DisconnectRequestedDate = clock.Today.AddMonths(-7),
            DisconnectEffectiveDate = clock.Today.AddMonths(-6),
            TechnicalNotes = "Replaced by the Spectrum coax service. Disconnect confirmed by the " +
                             "carrier — but the charge is still appearing on the invoice.",
        };
        services.Add(decommissioned);

        // POTS lines with no contract terms recorded anywhere.
        foreach ((Location site, string wtn, ServiceType type, string note) in new[]
        {
            (northgate, "(312) 555-0198", ServiceType.ElevatorLine, "Elevator emergency phone, car 1"),
            (hq, "(312) 555-0101", ServiceType.ElevatorLine, "Elevator emergency phone, bank A"),
            (columbus, "(614) 555-0133", ServiceType.AlarmLine, "Sprinkler supervisory dial-out"),
            (boston, "(617) 555-0144", ServiceType.FaxLine, "Records fax — retained for referrals"),
        })
        {
            services.Add(new TelecomService
            {
                ServiceType = type,
                LocationId = site.Id,
                CarrierVendorId = context.Granite.Id,
                VendorAccountId = context.GraniteAccount.Id,
                Status = ServiceStatus.Active,
                ServiceRole = ServiceRole.Standalone,
                Media = TransportMedia.Copper,
                HandoffType = HandoffType.Unknown,
                SupportPriority = SupportPriority.P3,
                TechnicalNotes = note,
                PhoneNumbers =
                [
                    new ServicePhoneNumber
                    {
                        NumberOrRangeStart = wtn,
                        Kind = type == ServiceType.FaxLine ? PhoneNumberKind.Fax : PhoneNumberKind.Alarm,
                        Description = note,
                    },
                ],
            });
        }

        db.TelecomServices.AddRange(services);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ── Dependencies: the fake-diversity findings, recorded with evidence.
        db.ServiceDependencies.AddRange(
            new ServiceDependency
            {
                ServiceId = northgateBackup.Id,
                DependsOnServiceId = northgatePrimary.Id,
                DependencyType = DependencyType.SharedLastMile,
                Confidence = DependencyConfidence.Confirmed,
                Evidence = "Letter of agency dated 2026-03-11 shows both circuits delivered over " +
                           "Everstream fibre into the same building entrance.",
                AssessedOn = clock.Today.AddMonths(-5),
                Notes = "A cut in the Broadway conduit takes both. Genuine diversity would need a " +
                        "different physical entrance — quoted at $14k of construction.",
            },
            new ServiceDependency
            {
                ServiceId = riversideBackup.Id,
                DependsOnServiceId = riversidePrimary.Id,
                DependencyType = DependencyType.SharedConduit,
                Confidence = DependencyConfidence.Suspected,
                Evidence = "Both terminate in the same basement MPOE rack. Building management has " +
                           "not confirmed whether the conduits are separate above ground.",
                AssessedOn = clock.Today.AddMonths(-2),
                Notes = "Awaiting a riser diagram from the property manager.",
            },
            new ServiceDependency
            {
                ServiceId = hqSecondary.Id,
                DependsOnServiceId = hqPrimary.Id,
                DependencyType = DependencyType.SharedBuildingEntrance,
                Confidence = DependencyConfidence.RuledOut,
                Evidence = "Diversity letter from both carriers, 2025-11-02. Separate entrance " +
                           "facilities on Madison and Wells, separate risers, verified on site.",
                AssessedOn = clock.Today.AddMonths(-9),
            });

        await SeedCostsAndContractsAsync(context, services, cancellationToken).ConfigureAwait(false);
        await SeedIpAssignmentsAsync(
            [northgatePrimary, northgateBackup, riversidePrimary, hqPrimary, hqSecondary, columbusPrimary],
            cancellationToken).ConfigureAwait(false);
        await SeedMonitoringAsync(context, services, cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedCostsAndContractsAsync(
        SeedContext context, List<TelecomService> services, CancellationToken cancellationToken)
    {
        DateOnly today = clock.Today;
        var random = new Random(20260819); // Fixed seed: the demo estate is the same every time.

        foreach (TelecomService service in services)
        {
            if (service.Status == ServiceStatus.Ordered)
            {
                continue;
            }

            decimal baseRate = service.ServiceType switch
            {
                ServiceType.Internet when service.Media == TransportMedia.Fiber =>
                    Math.Round(400m + (service.Bandwidth?.DownloadKbps ?? 0) / 1000m * 1.9m, 2),
                ServiceType.Internet when service.Media == TransportMedia.Coax => 389m,
                ServiceType.Internet when service.Media == TransportMedia.Copper => 612m, // legacy T1
                ServiceType.MplsVpn => 1_850m,
                ServiceType.CellularBackup => 145m,
                ServiceType.SipTrunk => 25.50m * (service.VoiceDetail?.ChannelCount ?? 24),
                ServiceType.Pri => 495m,
                ServiceType.ElevatorLine or ServiceType.AlarmLine or ServiceType.FaxLine => 87.40m,
                _ => 120m,
            };

            // A prior cost period, then the current one — so the cost-history panel has
            // something in it and the append-only behaviour is visible from the first click.
            decimal priorRate = Math.Round(baseRate * 0.94m, 2);

            db.ServiceCosts.Add(new ServiceCost
            {
                ServiceId = service.Id,
                EffectiveFrom = today.AddMonths(-26),
                EffectiveTo = today.AddMonths(-8),
                MonthlyRecurringCharge = priorRate,
                TaxesAndFees = Math.Round(priorRate * 0.06m, 2),
                CurrencyCode = "USD",
                BillingFrequency = BillingFrequency.Monthly,
                Source = CostSource.Contract,
            });

            db.ServiceCosts.Add(new ServiceCost
            {
                ServiceId = service.Id,
                EffectiveFrom = today.AddMonths(-8),
                EffectiveTo = null,
                MonthlyRecurringCharge = baseRate,
                TaxesAndFees = Math.Round(baseRate * 0.06m, 2),
                EquipmentRental = service.CpeManagedByCarrier ? random.Next(0, 3) * 15m : 0m,
                CurrencyCode = "USD",
                BillingFrequency = BillingFrequency.Monthly,
                Source = CostSource.Invoice,
            });
        }

        // ── Contracts: one confirmed, one unconfirmed, one with no terms at all.
        var lumenMsa = new Contract
        {
            ContractNumber = "MSA-2291",
            VendorId = context.Lumen.Id,
            Description = "Master service agreement — fibre DIA and MPLS",
            StartDate = today.AddMonths(-44),
            InitialTermMonths = 36,
            EndDate = today.AddMonths(4),
            RenewalType = RenewalType.AutoRenew,
            RenewalTermMonths = 12,
            AutoRenew = true,
            NoticePeriodDays = 120,
            EarlyTerminationTerms = "60% of remaining MRC for the balance of the term.",
            EarlyTerminationFormula = "0.60 × MRC × months remaining",
            MinimumCommitmentAmount = 30_000m,
            PriceEscalatorPercent = 3m,
            EscalatorCadence = EscalatorCadence.Annual,
            SlaSummary = "99.99% availability, 4-hour MTTR, credits at 5% of MRC per hour beyond.",
            Status = ContractStatus.Active,
        };

        var spectrumAgreement = new Contract
        {
            ContractNumber = "SPEC-8841",
            VendorId = context.Spectrum.Id,
            Description = "Business internet — national schedule",
            StartDate = today.AddMonths(-21),
            InitialTermMonths = 24,
            EndDate = today.AddMonths(2),
            RenewalType = RenewalType.EvergreenMonthToMonth,
            NoticePeriodDays = 30,
            SlaSummary = "99.9% availability. Credits on request only.",
            Status = ContractStatus.Active,
            Notes = "Section 4.2 says notice is due 'thirty days prior to the end of the " +
                    "then-current term'. After the first evergreen roll, what counts as the " +
                    "then-current term is genuinely ambiguous — legal has been asked.",
        };

        // Everything unknown. This is the state that quietly costs money for years.
        var potsLegacy = new Contract
        {
            ContractNumber = "POTS-LEGACY-04",
            VendorId = context.Granite.Id,
            Description = "Aggregated POTS lines — inherited at acquisition",
            StartDate = today.AddYears(-6),
            InitialTermMonths = 0,
            EndDate = null,
            RenewalType = RenewalType.Unknown,
            NoticePeriodDays = null,
            Status = ContractStatus.Active,
            Notes = "No signed agreement located. Lines may be month-to-month or on an " +
                    "auto-renewing schedule nobody has seen. Ask Granite for a copy.",
        };

        db.Contracts.AddRange(lumenMsa, spectrumAgreement, potsLegacy);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        lumenMsa.ProposedNoticeDeadlineDate = lumenMsa.EndDate!.Value.AddDays(-lumenMsa.NoticePeriodDays!.Value);
        lumenMsa.NoticeDeadlineDate = lumenMsa.ProposedNoticeDeadlineDate;
        lumenMsa.NoticeDeadlineConfirmed = true;
        lumenMsa.NoticeDeadlineConfirmedUtc = clock.UtcNow.AddMonths(-3);

        // Proposed but never confirmed — renders in the "needs review" state.
        spectrumAgreement.ProposedNoticeDeadlineDate =
            spectrumAgreement.EndDate!.Value.AddDays(-spectrumAgreement.NoticePeriodDays!.Value);
        spectrumAgreement.NoticeDeadlineConfirmed = false;

        foreach (TelecomService service in services)
        {
            Guid? contractId = service.CarrierVendorId switch
            {
                var id when id == context.Lumen.Id => lumenMsa.Id,
                var id when id == context.Spectrum.Id => spectrumAgreement.Id,
                var id when id == context.Granite.Id => potsLegacy.Id,
                _ => null,
            };

            if (contractId is { } value)
            {
                db.ContractServices.Add(new ContractService
                {
                    ContractId = value,
                    ServiceId = service.Id,
                    ServiceEndDate = value == lumenMsa.Id ? lumenMsa.EndDate : null,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedIpAssignmentsAsync(
        IReadOnlyList<TelecomService> services, CancellationToken cancellationToken)
    {
        // RFC 5737 documentation ranges. Never real address space, even in a demo — a
        // seeded "example" block that happens to belong to someone else is a bad habit
        // that eventually ships.
        string[] blocks =
        [
            "203.0.113.8/29", "203.0.113.16/29", "198.51.100.32/28",
            "198.51.100.64/29", "198.51.100.72/29", "203.0.113.40/29",
        ];

        for (int index = 0; index < services.Count && index < blocks.Length; index++)
        {
            string cidr = blocks[index];
            string network = cidr.Split('/')[0];
            string[] octets = network.Split('.');
            string prefix = $"{octets[0]}.{octets[1]}.{octets[2]}";
            int lastOctet = int.Parse(octets[3], System.Globalization.CultureInfo.InvariantCulture);

            db.ServiceIpAssignments.Add(new ServiceIpAssignment
            {
                ServiceId = services[index].Id,
                AddressFamily = AddressFamily.IPv4,
                CidrEncrypted = encryptor.Encrypt(cidr),
                GatewayEncrypted = encryptor.Encrypt($"{prefix}.{lastOctet + 1}"),
                UsableFirstEncrypted = encryptor.Encrypt($"{prefix}.{lastOctet + 2}"),
                UsableLastEncrypted = encryptor.Encrypt($"{prefix}.{lastOctet + 6}"),
                DnsPrimaryEncrypted = encryptor.Encrypt("8.8.8.8"),
                DnsSecondaryEncrypted = encryptor.Encrypt("1.1.1.1"),
                CidrSearchHash = encryptor.ComputeSearchHash(cidr),
                IsRoutedBlock = index % 3 == 0,
                AssignmentNotes = index % 3 == 0 ? "Routed block behind the WAN interface." : null,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedMonitoringAsync(
        SeedContext context, List<TelecomService> services, CancellationToken cancellationToken)
    {
        // Three perspectives: one cloud, two self-hosted in deliberately different failure
        // domains. Two agents are only two perspectives if they can fail independently —
        // same cluster, same UPS, or same upstream circuit makes them one perspective
        // wearing two hats, and the quorum rule would count it twice.
        var azureProbe = new Probe
        {
            Name = "Azure — East US 2",
            Kind = ProbeKind.AzureFunction,
            Status = ProbeStatus.Healthy,
            LastHeartbeatUtc = clock.UtcNow.AddMinutes(-1),
            AgentVersion = "1.0.0",
            FailureDomain = "Azure / eastus2",
        };

        var primaryAgent = new Probe
        {
            Name = "Agent — Dorchester DC",
            Kind = ProbeKind.SelfHostedAgent,
            LocationId = context.Locations[3].Id,
            Status = ProbeStatus.Healthy,
            LastHeartbeatUtc = clock.UtcNow.AddSeconds(-40),
            AgentVersion = "1.0.0",
            HostKind = AgentHostKind.WindowsService,
            HmacKeyVaultSecretName = "probe-hmac-dorchester-dc",
            FailureDomain = "Dorchester DC / cluster-A / feed-1",
        };

        // Offline on purpose. Its monitors go Unknown, not Down — which is the behaviour
        // the whole monitoring design is built around, and it should be visible on day one.
        var secondaryAgent = new Probe
        {
            Name = "Agent — Columbus DC",
            Kind = ProbeKind.SelfHostedAgent,
            LocationId = context.Locations[2].Id,
            Status = ProbeStatus.Offline,
            LastHeartbeatUtc = clock.UtcNow.AddHours(-9),
            AgentVersion = "1.0.0",
            HostKind = AgentHostKind.WindowsService,
            HmacKeyVaultSecretName = "probe-hmac-columbus-dc",
            FailureDomain = "Columbus DC / cluster-B / feed-2",
        };

        db.Probes.AddRange(azureProbe, primaryAgent, secondaryAgent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var monitors = new List<ServiceMonitor>();

        // Monitors only on data services with a circuit ID. Everything else is left
        // uncovered on purpose, so the "no monitoring coverage" tile has a real number.
        foreach (TelecomService service in services.Where(item =>
                     item.IsDataService && item.Status == ServiceStatus.Active && item.CircuitId is not null))
        {
            monitors.Add(new ServiceMonitor
            {
                ServiceId = service.Id,
                LocationId = service.LocationId,
                Name = $"{service.CircuitId} — public edge",
                TargetKind = MonitorTargetKind.PublicCircuitEndpoint,
                CheckType = CheckType.Icmp,
                Target = "203.0.113.1",
                IntervalSeconds = 60,
                TimeoutMs = 5_000,
                FailureThreshold = 3,
                SuccessThreshold = 2,
                RequiredProbeQuorum = 2,
                Enabled = true,
                CurrentState = MonitorState.Up,
                LastCheckedUtc = clock.UtcNow.AddSeconds(-30),
                StateChangedUtc = clock.UtcNow.AddDays(-14),
            });
        }

        // One internal always-on target per location, watched from an agent. The preferred
        // target is the branch firewall's LAN/management address — never a workstation or
        // printer, whose availability tracks somebody's working hours rather than the site's.
        //
        // Two locations are deliberately left without one, so the "no internal target"
        // coverage gap has something to report on day one. A site monitored only from the
        // outside can have every circuit answering while everything behind the firewall is
        // dark, and the reports must say so rather than imply we looked.
        var withoutInternalTarget = new[] { context.Locations[9].Id, context.Locations[11].Id };

        foreach (Location site in context.Locations)
        {
            if (withoutInternalTarget.Contains(site.Id))
            {
                continue;
            }

            monitors.Add(new ServiceMonitor
            {
                ServiceId = null,
                LocationId = site.Id,
                Name = $"{site.LocationCode} — internal reachability",
                TargetKind = MonitorTargetKind.InternalLocationTarget,
                InternalTargetDeviceKind = InternalTargetKind.FirewallLanOrManagement,
                CheckType = CheckType.Icmp,
                Target = "10.0.0.1",
                IntervalSeconds = 60,
                TimeoutMs = 5_000,
                FailureThreshold = 3,
                SuccessThreshold = 2,
                // One agent can see an internal target; Azure cannot reach it at all, so
                // requiring two would make every internal monitor permanently Unknown.
                RequiredProbeQuorum = 1,
                Enabled = true,
                CurrentState = MonitorState.Up,
                LastCheckedUtc = clock.UtcNow.AddSeconds(-25),
                StateChangedUtc = clock.UtcNow.AddDays(-30),
            });
        }

        db.Monitors.AddRange(monitors);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (ServiceMonitor monitor in monitors)
        {
            if (monitor.TargetKind == MonitorTargetKind.PublicCircuitEndpoint)
            {
                db.MonitorProbeAssignments.Add(new MonitorProbeAssignment
                {
                    MonitorId = monitor.Id, ProbeId = azureProbe.Id, Enabled = true,
                });
            }

            db.MonitorProbeAssignments.Add(new MonitorProbeAssignment
            {
                MonitorId = monitor.Id,
                ProbeId = monitor.LocationId == context.Locations[2].Id ? secondaryAgent.Id : primaryAgent.Id,
                Enabled = true,
            });
        }

        // Three historical outages, including one long enough to breach a 99.99% SLA.
        ServiceMonitor? northgateMonitor = monitors.FirstOrDefault();

        if (northgateMonitor is not null)
        {
            db.OutageEvents.AddRange(
                new OutageEvent
                {
                    MonitorId = northgateMonitor.Id,
                    ServiceId = northgateMonitor.ServiceId,
                    LocationId = northgateMonitor.LocationId,
                    StartUtc = clock.UtcNow.AddDays(-186).AddHours(-3),
                    EndUtc = clock.UtcNow.AddDays(-186).AddMinutes(-138),
                    ConfirmingProbeCount = 2,
                    Classification = OutageClassification.CarrierFailure,
                    ClassificationReason =
                        "Another service at this location was up, so the site had connectivity. " +
                        "The fault was with this carrier or its path.",
                    Cause = "Fibre cut — third-party excavation on N Broadway.",
                    CarrierTicketNumber = "LUM-8842119",
                    InternalTicketNumber = "INC-44120",
                    BusinessImpact = BusinessImpact.High,
                    SlaCreditStatus = SlaCreditStatus.Eligible,
                    CarrierNotifiedUtc = clock.UtcNow.AddDays(-186).AddHours(-3).AddMinutes(6),
                    CarrierFirstResponseUtc = clock.UtcNow.AddDays(-186).AddHours(-3).AddMinutes(34),
                    Notes = "Backup circuit carried traffic, but at 600/35 rather than 1G symmetric. " +
                            "Imaging uploads queued for the duration.",
                },
                new OutageEvent
                {
                    MonitorId = northgateMonitor.Id,
                    ServiceId = northgateMonitor.ServiceId,
                    LocationId = northgateMonitor.LocationId,
                    StartUtc = clock.UtcNow.AddDays(-290),
                    EndUtc = clock.UtcNow.AddDays(-290).AddMinutes(18),
                    ConfirmingProbeCount = 2,
                    Classification = OutageClassification.CarrierFailure,
                    ClassificationReason = "Single-circuit failure with a healthy sibling at the same site.",
                    Cause = "Carrier maintenance overran its window.",
                    IsPlanned = false,
                    BusinessImpact = BusinessImpact.Low,
                });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedRolePermissionsAsync(CancellationToken cancellationToken)
    {
        if (await db.RolePermissions.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Dictionary<string, int> roleIds = await db.Roles
            .ToDictionaryAsync(role => role.Name, role => role.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach ((string roleName, IReadOnlyList<string> permissions) in Roles.DefaultPermissions)
        {
            if (!roleIds.TryGetValue(roleName, out int roleId))
            {
                continue;
            }

            db.RolePermissions.AddRange(permissions.Select(permission =>
                new RolePermission { RoleId = roleId, Permission = permission }));
        }
    }

    private async Task SeedNotificationRulesAsync(CancellationToken cancellationToken)
    {
        if (await db.NotificationRules.IgnoreQueryFilters().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Every rule ships DISABLED, and stays that way until the initial import has been
        // reviewed and a test notification has been sent. A first import that fires four
        // hundred emails is how a rollout becomes an incident, and how the alerts get
        // filtered to a folder nobody reads before the tool has proved itself.
        //
        // Recipients below are placeholders in the example.org domain. They are deliberately
        // not real addresses: an accidentally-enabled rule should fail to deliver rather
        // than reach somebody.

        var renewal = new NotificationRule
        {
            Name = "Contract renewal and notice deadline",
            EventType = NotificationEventTypes.ContractNoticeDeadline,
            Channels = NotificationChannel.Email | NotificationChannel.Teams,
            NotifyRecordOwner = true,
            SharedMailbox = "telecom-procurement@example.org",
            TeamsChannelReference = "Telecom / Contracts",
            ThresholdDaysCsv = "180,120,90,60,30",
            Enabled = false,
            EscalationSteps =
            [
                new NotificationEscalationStep
                {
                    ThresholdDays = 60,
                    Condition = EscalationCondition.IfUnconfirmedOrNoAction,
                    RoleScope = Roles.Procurement,
                    Description =
                        "Sixty days out, chase only if the deadline is still unconfirmed or " +
                        "nobody has recorded a decision. A confirmed deadline with a recorded " +
                        "decision does not need escalating.",
                },
                new NotificationEscalationStep
                {
                    ThresholdDays = 30,
                    Condition = EscalationCondition.Always,
                    RoleScope = Roles.Procurement,
                    Recipients = "it-leadership@example.org",
                    Description =
                        "Thirty days out, escalate unconditionally to the owner, procurement, " +
                        "and IT leadership. At this point the cost of a missed deadline exceeds " +
                        "the cost of an unnecessary email.",
                },
            ],
        };

        var outage = new NotificationRule
        {
            Name = "Outage confirmed",
            EventType = NotificationEventTypes.OutageConfirmed,
            Channels = NotificationChannel.Teams | NotificationChannel.Email,
            TeamsChannelReference = "IT Operations / Alerts",
            SharedMailbox = "helpdesk@example.org",
            RoleScope = Roles.HelpDesk,
            Enabled = false,
            // No thresholds: this fires on confirmation, and confirmation means the
            // correlation engine's quorum and debounce rules were satisfied. A single
            // advisory syslog event from The Dude cannot reach this rule — an ingested
            // probe raises Suspect, never Down.
        };

        db.NotificationRules.AddRange(
            renewal,
            outage,
            new NotificationRule
            {
                Name = "Invoice variance detected",
                EventType = NotificationEventTypes.InvoiceVarianceDetected,
                Channels = NotificationChannel.Email,
                SharedMailbox = "telecom-procurement@example.org",
                RoleScope = Roles.Procurement,
                Enabled = false,
                ThresholdConfigJson = """{"variancePercentThreshold":10}""",
            },
            new NotificationRule
            {
                Name = "Integration sync failed",
                EventType = NotificationEventTypes.IntegrationSyncFailed,
                Channels = NotificationChannel.Email,
                RoleScope = Roles.AppAdministrator,
                Enabled = false,
            },
            new NotificationRule
            {
                Name = "Probe offline — monitoring coverage loss",
                EventType = NotificationEventTypes.ProbeOffline,
                Channels = NotificationChannel.Teams,
                TeamsChannelReference = "IT Operations / Alerts",
                RoleScope = Roles.NetworkEngineer,
                Enabled = false,
                // Worded as coverage loss on purpose. A probe going quiet means we stopped
                // being able to see those locations, not that they went down — and an alert
                // that says "site down" when it means "we are blind" burns credibility fast.
            });
    }

    // ── Small builders, kept private to this file ────────────────────────────────────

    private static Vendor Carrier(
        string legalName, string displayName, string portal, string phone, string hours, string credentialRef) =>
        new()
        {
            LegalName = legalName,
            DisplayName = displayName,
            Kind = VendorKind.Carrier,
            PortalUrl = portal,
            MainSupportPhone = phone,
            SupportHours = hours,
            CredentialReference = credentialRef,
        };

    private static Contact InternalContact(string name, string title, string email, string phone) =>
        new()
        {
            FullName = name, JobTitle = title, Email = email,
            PhoneNumber = phone, Kind = ContactKind.Internal,
        };

    private static Contact VendorContact(
        Guid vendorId, string name, string title, ContactKind kind,
        string? email, string? phone, int? escalation) =>
        new()
        {
            VendorId = vendorId, FullName = name, JobTitle = title, Kind = kind,
            Email = email, PhoneNumber = phone, EscalationLevel = escalation,
        };

    private static VendorAccount Account(
        Guid vendorId, string accountNumber, string? billingAccountNumber, string description) =>
        new()
        {
            VendorId = vendorId,
            AccountNumber = accountNumber,
            BillingAccountNumber = billingAccountNumber,
            Description = description,
        };

    private static Location Site(
        string code, string name, LocationType type, string line1, string city, string state,
        string postal, string timeZone, Region region, BusinessUnit unit, CostCenter costCenter,
        Criticality criticality, int acceptableOutageMinutes, Contact itOwner,
        decimal latitude, decimal longitude) =>
        new()
        {
            LocationCode = code,
            Name = name,
            LocationType = type,
            Status = LocationStatus.Active,
            PhysicalAddress = new Address
            {
                Line1 = line1, City = city, StateOrProvince = state,
                PostalCode = postal, CountryCode = "US",
            },
            TimeZoneId = timeZone,
            RegionId = region.Id,
            BusinessUnitId = unit.Id,
            CostCenterId = costCenter.Id,
            ItOwnerContactId = itOwner.Id,
            Criticality = criticality,
            AcceptableOutageMinutes = acceptableOutageMinutes,
            Latitude = latitude,
            Longitude = longitude,
            OperatingHours = type == LocationType.Warehouse ? "M–F 06:00–22:00" : "M–F 07:00–19:00, Sat 08:00–13:00",
        };

    private static TelecomService Circuit(
        Location location, Vendor carrier, VendorAccount account, ServiceType type, ServiceRole role,
        string? circuitId, TransportMedia media, HandoffType handoff, string? demarc,
        string? cpeMake, string? cpeModel, string? cpeSerial, bool carrierManaged, string? wanInterface,
        SupportPriority priority, int downloadKbps, int uploadKbps, int cirKbps,
        decimal? slaAvailability, int? slaLatencyMs, Vendor? lastMile) =>
        new()
        {
            ServiceType = type,
            LocationId = location.Id,
            CarrierVendorId = carrier.Id,
            LastMileVendorId = lastMile?.Id,
            VendorAccountId = account.Id,
            CircuitId = circuitId,
            Status = ServiceStatus.Active,
            ServiceRole = role,
            Media = media,
            HandoffType = handoff,
            DemarcLocation = demarc,
            CpeMake = cpeMake,
            CpeModel = cpeModel,
            CpeSerial = cpeSerial,
            CpeManagedByCarrier = carrierManaged,
            WanInterface = wanInterface,
            SupportPriority = priority,
            InstallDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-30),
            ActivationDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-30).AddDays(7),
            Bandwidth = new ServiceBandwidth
            {
                DownloadKbps = downloadKbps,
                UploadKbps = uploadKbps,
                CommittedInformationRateKbps = cirKbps,
                SlaAvailabilityPercent = slaAvailability,
                SlaLatencyMs = slaLatencyMs,
            },
        };

    private static TelecomService VoiceCircuit(
        Location location, Vendor carrier, VendorAccount account, ServiceType type,
        string circuitId, int channels, string btn) =>
        new()
        {
            ServiceType = type,
            LocationId = location.Id,
            CarrierVendorId = carrier.Id,
            VendorAccountId = account.Id,
            CircuitId = circuitId,
            Status = ServiceStatus.Active,
            ServiceRole = ServiceRole.Standalone,
            Media = type == ServiceType.Pri ? TransportMedia.Copper : TransportMedia.Fiber,
            HandoffType = type == ServiceType.Pri ? HandoffType.T1Rj48 : HandoffType.Rj45,
            SupportPriority = SupportPriority.P2,
            VoiceDetail = new VoiceServiceDetail
            {
                ChannelCount = channels,
                BillingTelephoneNumber = btn,
                E911RegisteredAddress = location.PhysicalAddress.SingleLine,
                E911LastVerifiedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-14),
            },
            PhoneNumbers =
            [
                new ServicePhoneNumber
                {
                    NumberOrRangeStart = btn, Kind = PhoneNumberKind.Main, Description = "Main billing number",
                },
            ],
        };
}
