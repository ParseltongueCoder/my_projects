namespace Bo.Core.Security;

public enum RiskLevel
{
    Normal,
    Sensitive,
    Critical,
}

public sealed record PermissionDef(string Code, string Module, string Description, RiskLevel Risk = RiskLevel.Normal, bool PlatformOnly = false);

public sealed record SystemRole(string Code, string Name, bool IsPlatform, IReadOnlyList<string> Permissions);

/// <summary>
/// The permission catalog (docs/08 §3.3, docs/10 §8). Code is the source of truth; Bo.Api syncs it into
/// <c>bo.permission</c> / <c>bo.role</c> at startup. Permissions are checked on the API, never only in the UI.
/// </summary>
public static class Permissions
{
    public const string CfgView = "cfg.view";
    public const string CfgEdit = "cfg.edit";
    public const string CfgApprove = "cfg.approve";
    public const string AdmUserView = "adm.user.view";
    public const string AdmUserEdit = "adm.user.edit";
    public const string AdmRoleEdit = "adm.role.edit";
    public const string AdmAuditView = "adm.audit.view";
    public const string BrandView = "adm.brand.view";
    public const string PlatformOperatorManage = "platform.operator.manage";
    public const string PlatformSettingsEdit = "platform.settings.edit";
    public const string PlatformImpersonateRead = "platform.impersonate_read";
    public const string PlatformImpersonateWrite = "platform.impersonate_write";

    public static readonly IReadOnlyList<PermissionDef> All =
    [
        new("cat.view", "CAT", "View catalog, events, participants"),
        new("cat.edit", "CAT", "Visibility, ordering, renames and overrides"),
        new("cat.event.create_manual", "CAT", "Create manual events", RiskLevel.Sensitive),
        new("cat.market.add_manual", "CAT", "Add manual markets", RiskLevel.Sensitive),
        new("cat.media.upload", "CAT", "Upload logos and icons"),
        new("i18n.view", "I18N", "View translations"),
        new("i18n.edit", "I18N", "Edit translations"),
        new("i18n.import", "I18N", "Bulk import translations"),
        new("odds.view", "ODDS", "View prices and trading view"),
        new("odds.suspend", "ODDS", "Suspend / close events and markets", RiskLevel.Sensitive),
        new("odds.override", "ODDS", "Manual odds overrides", RiskLevel.Sensitive),
        new("odds.margin.edit", "ODDS", "Edit margins", RiskLevel.Sensitive),
        new(CfgView, "CFG", "View configuration"),
        new(CfgEdit, "CFG", "Change configuration (creates change sets)", RiskLevel.Sensitive),
        new(CfgApprove, "CFG", "Approve configuration change sets of other users", RiskLevel.Sensitive),
        new("cms.view", "CMS", "View messages and content"),
        new("cms.edit", "CMS", "Edit messages and content"),
        new("bet.view", "BET", "Search and view tickets"),
        new("bet.view_pii", "BET", "See customer personal data on tickets", RiskLevel.Sensitive),
        new("bet.void", "BET", "Void tickets", RiskLevel.Critical),
        new("bet.settle_manual", "BET", "Manual settlement", RiskLevel.Critical),
        new("bet.resettle", "BET", "Resettle tickets", RiskLevel.Critical),
        new("limit.view", "LIM", "View limits"),
        new("limit.edit", "LIM", "Edit limits", RiskLevel.Sensitive),
        new("limit.customer.edit", "LIM", "Edit customer limits and stake factors", RiskLevel.Sensitive),
        new("liability.view", "LIM", "View liability"),
        new("cashout.view", "CASH", "View cash-out configuration"),
        new("cashout.edit", "CASH", "Enable / disable cash-out", RiskLevel.Sensitive),
        new("customer.view", "CUS", "View customers"),
        new("customer.view_pii", "CUS", "View customer personal data", RiskLevel.Sensitive),
        new("customer.edit", "CUS", "Edit customer profile, risk group, tags"),
        new("customer.restrict", "CUS", "Restrict customers", RiskLevel.Sensitive),
        new("customer.note", "CUS", "Add customer notes"),
        new("monitor.view", "MON", "Live bet monitoring dashboards"),
        new("referral.decide", "MON", "Decide referred bets (within role constraints)", RiskLevel.Sensitive),
        new("referral.decide_over_limit", "MON", "Decide referred bets above role constraints", RiskLevel.Critical),
        new("referral.steal", "MON", "Take over a referral claimed by someone else"),
        new("report.view", "REP", "View reports and KPI dashboard"),
        new("report.financial", "REP", "Financial reports (GGR, NGR)", RiskLevel.Sensitive),
        new("report.export", "REP", "Export reports"),
        new("report.schedule", "REP", "Schedule reports"),
        new("report.regulatory", "REP", "Regulatory reports", RiskLevel.Sensitive),
        new("promo.view", "PROMO", "View campaigns and freebets"),
        new("promo.campaign.edit", "PROMO", "Edit campaigns"),
        new("promo.campaign.activate", "PROMO", "Activate campaigns", RiskLevel.Sensitive),
        new("promo.freebet.grant", "PROMO", "Grant freebets", RiskLevel.Sensitive),
        new("promo.freebet.cancel", "PROMO", "Cancel freebets", RiskLevel.Sensitive),
        new("notif.view", "NOTIF", "View alerts"),
        new("notif.rule.edit", "NOTIF", "Edit alert rules"),
        new("notif.channel.edit", "NOTIF", "Edit alert channels"),
        new(AdmUserView, "ADM", "View back-office users"),
        new(AdmUserEdit, "ADM", "Invite, disable users and grant roles", RiskLevel.Critical),
        new(AdmRoleEdit, "ADM", "Edit custom roles", RiskLevel.Critical),
        new(AdmAuditView, "ADM", "View audit log"),
        new(BrandView, "ADM", "View brands (sites) of the operator"),
        new("adm.security.edit", "ADM", "Security policy (2FA, IP allow-list)", RiskLevel.Critical),
        new("adm.approval.decide", "ADM", "Decide four-eyes approval requests", RiskLevel.Sensitive),
        new(PlatformOperatorManage, "PLATFORM", "Create and manage operators and brands", RiskLevel.Critical, PlatformOnly: true),
        new(PlatformSettingsEdit, "PLATFORM", "Edit platform-wide settings", RiskLevel.Critical, PlatformOnly: true),
        new(PlatformImpersonateRead, "PLATFORM", "Act as an operator (read)", RiskLevel.Sensitive, PlatformOnly: true),
        new(PlatformImpersonateWrite, "PLATFORM", "Act as an operator (write)", RiskLevel.Critical, PlatformOnly: true),
        new("platform.report.cross_operator", "PLATFORM", "Cross-operator reports", RiskLevel.Sensitive, PlatformOnly: true),
    ];

    private static readonly HashSet<string> Known = All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string code) => Known.Contains(code);

    private static string[] Where(Func<PermissionDef, bool> predicate) => All.Where(predicate).Select(p => p.Code).ToArray();

    private static string[] Modules(params string[] modules) => Where(p => modules.Contains(p.Module));

    private static readonly string[] OperatorAll = Where(p => !p.PlatformOnly);
    private static readonly string[] ViewOnly = Where(p => !p.PlatformOnly && (p.Code.EndsWith(".view", StringComparison.Ordinal)));

    /// <summary>System roles (docs/08 §3.3; head_trader from docs/09 §2.15).</summary>
    public static readonly IReadOnlyList<SystemRole> SystemRoles =
    [
        new("operator_admin", "Operator admin", false, OperatorAll),
        new("head_trader", "Head trader", false,
        [
            .. Modules("CAT", "ODDS", "CASH", "MON"), "cfg.view", "cfg.edit", "cfg.approve", "limit.view", "limit.edit",
            "liability.view", "bet.view", "bet.settle_manual", "bet.resettle", "report.view", "adm.brand.view",
        ]),
        new("trader", "Trader", false,
        [
            .. Modules("CAT", "ODDS", "CASH"), "cfg.view", "cfg.edit", "limit.view", "limit.edit", "liability.view",
            "bet.view", "monitor.view", "referral.decide", "report.view", "adm.brand.view",
        ]),
        new("risk_manager", "Risk manager", false,
        [
            .. Modules("LIM", "MON"), "cfg.view", "customer.view", "customer.restrict", "customer.note", "bet.view", "bet.void",
            "notif.view", "notif.rule.edit", "report.view", "report.financial", "adm.brand.view",
        ]),
        new("customer_support", "Customer support", false,
            ["customer.view", "customer.view_pii", "customer.note", "bet.view", "promo.view", "promo.freebet.grant", "adm.brand.view"]),
        new("marketing", "Marketing", false, [.. Modules("PROMO"), "customer.view", "report.view", "adm.brand.view"]),
        new("finance", "Finance", false, [.. Modules("REP"), "bet.view", "promo.view", "adm.brand.view"]),
        new("content_manager", "Content manager", false, ["cat.view", "cat.edit", "cat.media.upload", .. Modules("I18N", "CMS"), "adm.brand.view"]),
        new("auditor", "Auditor", false, [.. ViewOnly, AdmAuditView]),
        new("platform_superadmin", "Platform super admin", true, [.. Where(_ => true)]),
        new("platform_support", "Platform support", true, [.. ViewOnly, AdmAuditView, PlatformImpersonateRead]),
        new("platform_readonly", "Platform read-only", true, [.. ViewOnly, PlatformImpersonateRead]),
    ];

    /// <summary>Permissions that only change data (used to keep impersonating platform staff read-only).</summary>
    public static bool IsWrite(string code) =>
        !code.EndsWith(".view", StringComparison.Ordinal) && code is not (AdmAuditView or PlatformImpersonateRead)
        && !code.StartsWith("platform.", StringComparison.Ordinal);
}
