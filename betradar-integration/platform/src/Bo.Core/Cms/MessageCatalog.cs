namespace Bo.Core.Cms;

public sealed record MessageText(string Title, string Text);

/// <summary>A reason code the player can get back, with its parameters and our default texts (docs/06 §6.2).</summary>
public sealed record MessageDef(
    string Code,
    string Module,
    string Category,
    string Severity,
    IReadOnlyList<string> Params,
    MessageText En,
    MessageText Ka,
    string Description,
    bool CustomerVisible = true)
{
    public MessageText? Default(string lang) => lang switch
    {
        "en" => En,
        "ka" => Ka,
        _ => null,
    };
}

/// <summary>
/// Reason codes of BET, LIM, INT, CUS, CASH and MON (referral). Code is the source of truth and is synced into
/// <c>bo.message_def</c> at startup; the owning module decides when a code is returned, CMS only owns the text.
/// We always ship ka and en; operators override per language and brand. Parameters use <c>{name}</c> or
/// <c>{name, number}</c> (two decimals).
/// </summary>
public static class MessageCatalog
{
    private static MessageDef M(string code, string module, string category, string[] ps, (string, string) en, (string, string) ka,
        string description, string severity = "error") =>
        new(code, module, category, severity, ps, new MessageText(en.Item1, en.Item2), new MessageText(ka.Item1, ka.Item2), description);

    public static readonly IReadOnlyList<MessageDef> All =
    [
        // BET
        M("BET_ODDS_CHANGED", "BET", "bet_reject", ["newOdds"],
            ("Odds changed", "The odds changed to {newOdds}. Please confirm your bet again."),
            ("კოეფიციენტი შეიცვალა", "კოეფიციენტი შეიცვალა: {newOdds}. გთხოვთ, ფსონი ხელახლა დაადასტუროთ."),
            "Odds moved against the player beyond bet.odds_change_policy", "warning"),
        M("BET_MARKET_SUSPENDED", "BET", "bet_reject", [],
            ("Betting is paused", "Betting on this selection is temporarily paused."),
            ("ფსონები შეჩერებულია", "ამ არჩევანზე ფსონის დადება დროებით შეჩერებულია."),
            "Market suspended by the feed, a trader or the sanity check"),
        M("BET_SELECTION_UNAVAILABLE", "BET", "bet_reject", [],
            ("Selection unavailable", "This selection is no longer available."),
            ("არჩევანი მიუწვდომელია", "ეს არჩევანი აღარ არის ხელმისაწვდომი."),
            "Market or outcome hidden, closed, deactivated or settled"),
        M("BET_EVENT_STARTED", "BET", "bet_reject", [],
            ("Event started", "This event has already started."),
            ("ღონისძიება დაიწყო", "ეს ღონისძიება უკვე დაიწყო."),
            "Prematch selection after the start, live betting not offered"),
        M("BET_COMBO_TOO_MANY", "BET", "validation", ["maxSelections"],
            ("Too many selections", "An accumulator can have at most {maxSelections} selections."),
            ("ზედმეტად ბევრი არჩევანი", "ექსპრესში შეიძლება იყოს მაქსიმუმ {maxSelections} არჩევანი."),
            "bet.combo.max_selections exceeded"),
        M("BET_COMBO_SAME_EVENT", "BET", "validation", [],
            ("Not combinable", "These selections cannot be combined in one bet."),
            ("ვერ გაერთიანდება", "ეს არჩევანები ერთ ფსონში ვერ გაერთიანდება."),
            "Several selections from one event while bet.combo.allow_same_event is off"),

        // LIM: no risk details towards the player (docs/06 §6.5)
        M("LIM_MIN_STAKE", "LIM", "bet_reject", ["minStake", "currency"],
            ("Stake too low", "The minimum stake is {minStake, number} {currency}."),
            ("ფსონი ძალიან მცირეა", "მინიმალური ფსონია {minStake, number} {currency}."),
            "Below limit.min_stake"),
        M("LIM_MAX_STAKE_EXCEEDED", "LIM", "bet_reject", ["maxStake", "currency"],
            ("Stake too high", "The maximum stake on this selection is {maxStake, number} {currency}."),
            ("ფსონი ზღვარს აღემატება", "მაქსიმალური ფსონი ამ არჩევანზე არის {maxStake, number} {currency}."),
            "Above limit.max_stake (after stake factors)"),
        M("LIM_MAX_PAYOUT_EXCEEDED", "LIM", "bet_reject", ["maxPayout", "currency"],
            ("Potential win too high", "The maximum payout per bet is {maxPayout, number} {currency}."),
            ("მოგება ზღვარს აღემატება", "მაქსიმალური მოგება ერთ ფსონზე არის {maxPayout, number} {currency}."),
            "Above limit.max_payout"),
        M("LIM_NOT_ACCEPTED", "LIM", "bet_reject", [],
            ("Bet not accepted", "The bet cannot be accepted. Please try a lower stake."),
            ("ფსონი ვერ მიიღება", "ფსონი ვერ მიიღება. სცადეთ ნაკლები თანხა."),
            "Liability limits reached (never say so to the player)"),

        // INT (PAM)
        M("INT_INSUFFICIENT_FUNDS", "INT", "bet_reject", [],
            ("Insufficient balance", "Your balance is too low for this stake."),
            ("ბალანსი არასაკმარისია", "ბალანსი არ არის საკმარისი ამ ფსონისთვის."),
            "PAM debit refused for funds"),
        M("INT_WALLET_UNAVAILABLE", "INT", "system", [],
            ("Please try again", "We could not reach your account. Please try again in a moment."),
            ("სცადეთ თავიდან", "ანგარიშთან კავშირი ვერ მოხერხდა. სცადეთ ცოტა ხანში."),
            "PAM timeout or error"),

        // CUS (regulatory snapshot, docs/09 §2.10)
        M("CUS_SELF_EXCLUDED", "CUS", "bet_reject", [],
            ("Account restricted", "Betting is not available: your account is self-excluded."),
            ("ანგარიში შეზღუდულია", "ფსონის დადება შეუძლებელია: ანგარიშზე მოქმედებს თვითშეზღუდვა."),
            "Self-exclusion reported by the PAM"),
        M("CUS_UNDERAGE", "CUS", "bet_reject", ["minAge"],
            ("Not allowed", "Betting is allowed from the age of {minAge}."),
            ("დაუშვებელია", "ფსონის დადება დაშვებულია {minAge} წლიდან."),
            "Below rg.min_age"),
        M("CUS_RESTRICTED", "CUS", "bet_reject", [],
            ("Account restricted", "Betting is restricted on your account. Please contact support."),
            ("ანგარიში შეზღუდულია", "თქვენს ანგარიშზე ფსონის დადება შეზღუდულია. დაუკავშირდით მხარდაჭერას."),
            "Customer restriction set in the back office"),

        // CASH
        M("CASH_NOT_AVAILABLE", "CASH", "cashout", [],
            ("Cash-out unavailable", "Cash-out is not available for this bet right now."),
            ("ქეშაუთი მიუწვდომელია", "ამ ფსონზე ქეშაუთი ახლა ხელმისაწვდომი არ არის."),
            "cashout.enabled off, market suspended or below cashout.min_value"),
        M("CASH_VALUE_CHANGED", "CASH", "cashout", ["newValue", "currency"],
            ("Cash-out value changed", "The cash-out value is now {newValue, number} {currency}."),
            ("ქეშაუთის თანხა შეიცვალა", "ქეშაუთის თანხა ახლა არის {newValue, number} {currency}."),
            "Price moved between quote and request", "warning"),

        // MON / referral (docs/10)
        M("REF_PENDING_REVIEW", "MON", "referral", ["seconds"],
            ("Bet under review", "Your bet is being reviewed. This takes up to {seconds} seconds."),
            ("ფსონი მოწმდება", "თქვენი ფსონი მოწმდება. ეს გრძელდება მაქსიმუმ {seconds} წამს."),
            "Bet referred to a trader (player status pending_review)", "info"),
        M("REF_COUNTER_OFFER", "MON", "referral", ["stake", "odds", "currency", "seconds"],
            ("Counter-offer", "We can accept {stake, number} {currency} at odds {odds}. Please answer within {seconds} seconds."),
            ("კონტრშეთავაზება", "შეგვიძლია მივიღოთ {stake, number} {currency} კოეფიციენტით {odds}. გთხოვთ, უპასუხოთ {seconds} წამში."),
            "Trader counter-offer (referral.counter_offer_timeout_seconds)", "info"),
        M("REF_REJECTED", "MON", "referral", [],
            ("Bet not accepted", "Your bet was not accepted."),
            ("ფსონი არ მიიღება", "თქვენი ფსონი არ იქნა მიღებული."),
            "Rejected by a trader or on timeout"),
        M("REF_CANCELLED", "MON", "referral", [],
            ("Bet cancelled", "The review ended because the market closed."),
            ("ფსონი გაუქმდა", "შემოწმება შეწყდა, რადგან მარკეტი დაიხურა."),
            "Market closed or suspended during review (referral.on_market_suspend)"),

        M("SYS_UNAVAILABLE", "BET", "system", [],
            ("Temporarily unavailable", "Betting is temporarily unavailable. Please try again."),
            ("დროებით მიუწვდომელია", "ფსონის დადება დროებით შეუძლებელია. სცადეთ თავიდან."),
            "Any internal failure"),
    ];

    public static readonly IReadOnlyDictionary<string, MessageDef> ByCode = All.ToDictionary(m => m.Code, StringComparer.Ordinal);
}
