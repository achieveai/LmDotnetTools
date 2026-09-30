"""Prose for tr1: the policy, the maintenance calendar and the tickets (Tamberlow Hosting, fictional).

Every ticket class shares the vocabulary a keyword classifier would reach for: outage, down,
unavailable, CDN, DNS, firewall, staging, maintenance, minutes. The class shows only in what the
narrative says happened, often through a negation ("we checked our CDN partner first; it was
healthy"), a lay description of the symptom, or a title that points elsewhere.
"""
from datetime import timedelta

AGENTS = ["Priya Ashcombe", "Tomas Vell", "Ines Marrow", "Kofi Brandt", "Hanne Solberg", "Dev Rathmore",
          "Carys Pennick", "Oskar Lind"]
MAINT_SCOPES = ["storage firmware upgrade", "database minor-version upgrade", "load-balancer certificate rotation",
                "network switch replacement in the primary region", "hypervisor patching", "object-store migration",
                "kernel security patch rollout", "monitoring agent upgrade", "backup system re-indexing"]
SYL_A = ["Brack", "Quen", "Tams", "Morr", "Fell", "Hask", "Wend", "Dray", "Lum", "Corr", "Pell", "Rav", "Sten",
         "Ask", "Holm", "Varn", "Kest", "Mab", "Ord", "Tull", "Yarr", "Glen", "Pask", "Bex"]
SYL_B = ["ley", "wick", "ford", "by", "mere", "thorpe", "combe", "shaw", "den", "ham", "well", "ridge"]
TRADES = ["Opticians", "Cycle Works", "Veterinary Group", "Bakery Co.", "Lettings", "Physio Clinics", "Garden Centre",
          "Print Studio", "Tutoring", "Dental Practice", "Brewing Co.", "Removals", "Pet Supplies", "Kitchens",
          "Architects", "Travel", "Language School", "Florists", "Climbing Centre", "Audio Hire", "Bookshop",
          "Van Hire", "Tea Rooms", "Joinery"]
FIRST = ["Aoife", "Ben", "Chidi", "Dana", "Elif", "Fergus", "Gita", "Hugo", "Isla", "Jonah", "Keira", "Luca", "Maren",
         "Niall", "Olu", "Petra", "Rafe", "Sunita", "Theo", "Uma", "Vince", "Wren", "Yusuf", "Zara", "Callum", "Mei",
         "Rory", "Selin", "Tobias", "Nadia"]
PRODUCTS = ["online shop", "booking system", "customer portal", "members' area", "ordering site", "appointment app",
            "stock and orders API", "e-learning site", "quote builder", "click-and-collect site"]
PARTNER = {"cdn": "Veltrane Edge", "dns": "Orrisway DNS"}
OWN = {"cdn": "Fennick Edge", "dns": "Dunmore Domains", "sso": "Keyholt SSO"}
COMPANY = "Tamberlow Hosting"


def customer_names(rng, n):
    out = []
    while len(out) < n:
        name = f"{rng.choice(SYL_A)}{rng.choice(SYL_B)} {rng.choice(TRADES)}"
        if name not in out and name.split()[0] not in [o.split()[0] for o in out]:
            out.append(name)
    return out


def contact_names(rng, n):
    return [f"{rng.choice(FIRST)} {rng.choice(SYL_A)}{rng.choice(SYL_B)}" for _ in range(n)]


def slug(name):
    return name.split()[0].lower()


def dlong(d):
    return f"{d:%A} {d.day} {d:%B} {d.year}"


def dshort(d):
    return f"{d.day} {d:%B}"


def hm(d):
    return f"{d:%H:%M}"


# ============================================================================================ policy
def policy():
    return f"""# {COMPANY} — Service Credit Policy for Managed Hosting

Edition 4.1, applies to incidents that begin in the first quarter of 2026 (1 January to 31 March 2026).
Owner: Head of Customer Operations. Audience: support staff, account managers and the quarterly credit review panel.

## 1. Purpose and scope

1.1 This policy decides which support tickets record a Qualifying Incident, how long each one lasted for credit
purposes, and how the resulting service credits are calculated. The credit review panel applies it once per quarter
to every ticket opened in the quarter or reporting an incident in the quarter.

1.2 It covers the managed hosting service only. Domain registration, professional services days, training courses
and the partner marketplace are governed by their own terms and never produce a Qualifying Incident, even when a
ticket about them is filed through the same support portal.

1.3 Nothing in this policy limits a customer's statutory rights. Where a customer's contract contains a bespoke
service level, the bespoke terms apply instead and the account manager must say so in the ticket; no such contract
was in force for any account during the first quarter of 2026.

1.4 Service credits are the customer's sole financial remedy for unavailability of the service. They are applied
to the next invoice and are not paid out in cash.

## 2. Definitions

2.1 **Service.** The hosting of a customer's application or site on the {COMPANY} platform, including the platform's
load balancers, compute, storage, databases and the routing that carries traffic to them.

2.2 **Production Service.** A customer environment that serves the customer's own users or customers. Staging,
test, preview, user-acceptance and sandbox environments are not Production Services, whatever they are called by
the customer and even when they share a plan with a Production Service. Unavailability of an environment that is
not a Production Service never produces a Qualifying Incident.

2.3 **Unavailability.** A period in which a Production Service cannot be reached, or returns errors for all
requests, from outside the customer's own network. Slow responses, partial errors, individual failing pages and
problems affecting a single user or office are not Unavailability.

2.4 **Measured Minutes.** The length of a period of Unavailability as recorded by {COMPANY}'s external monitoring,
from the first failed check to the first successful check afterwards, as stated by support staff in the ticket.
Estimates given by the customer, by the customer's own monitoring tools or by third parties are not used to measure
an incident, even when they are longer or more precise.

2.5 **Qualifying Minutes.** Measured Minutes after the deductions in clause 5.

2.6 **Qualifying Incident.** A period of Unavailability that meets clauses 3, 4 and 6. Each Qualifying Incident
earns one service credit under clause 7.

2.7 **Written Instruction.** Advice given by {COMPANY} support staff in a reply to a support ticket. Advice given by
telephone, video call, webinar, conference talk or in person is not Written Instruction, even if the customer or the
staff member later describes it in writing.

## 3. Duration thresholds

3.1 An incident is a Qualifying Incident only if its Qualifying Minutes are 30 or more.

3.2 For accounts on the Enterprise plan, the threshold in clause 3.1 is 15 Qualifying Minutes instead of 30, for
incidents that begin on or after 1 February 2026. The date that decides which threshold applies is the date on
which the Unavailability began, not the date on which the ticket was opened or answered. Incidents that began before
1 February 2026 use the threshold in clause 3.1 whatever the plan.

3.3 The thresholds are applied to Qualifying Minutes, that is after the deductions in clause 5, never to Measured
Minutes directly.

3.4 Unavailability that stops and restarts within five minutes is one period; otherwise each period is judged on
its own. The panel does not add separate periods together to reach a threshold.

## 4. Causes

4.1 **Platform faults count.** Unavailability caused by a fault in the {COMPANY} platform, or by a change that
{COMPANY} made to it, counts towards a Qualifying Incident.

4.2 **Customer-side causes.** Unavailability caused by the customer does not count. This includes changes to the
customer's own firewall, allow-lists, credentials, certificates, DNS records that the customer manages, application
code, configuration or deployment pipeline, and suspension of an account for non-payment. It does count, however, if
the change that caused the Unavailability was made by following a Written Instruction; in that case the incident is
treated as a platform fault under clause 4.1. The panel must be able to find the Written Instruction in a ticket.

4.3 **Third-party providers.** Unavailability caused by the failure of a third-party provider, including
content-delivery networks, DNS providers, identity providers and payment services, is not a {COMPANY} fault and does
not count.

4.4 **The customer's own connectivity.** Unavailability that is visible only from the customer's own offices or
networks, including failures inside the customer's internet service provider, is not Unavailability under clause
2.3 and does not count.

4.5 Where more than one cause contributed, the panel decides by the cause named in the ticket as the root cause
by support staff after investigation, not by the cause the customer first suspected.

## 5. Planned maintenance

5.1 {COMPANY} carries out planned maintenance in windows published on the status page (the maintenance calendar).
A window is an announced window only if it was posted at least 72 hours before it began. A window posted later than
that, including emergency work, is not an announced window and produces no deduction.

5.2 Measured Minutes that fall inside an announced window are deducted from the incident before any threshold in
clause 3 is applied. Only the minutes inside the window are deducted: if the work overran, or the Unavailability
continued after the window closed, the minutes after the window's published end remain in the incident.

5.3 This clause takes precedence over clauses 3 and 4: an incident caused by a platform fault during an announced
window is judged only on its minutes outside the window.

5.4 Customers are asked to subscribe to the status page. Not having read an announcement does not change whether a
window was announced.

## 6. Reporting

6.1 An incident counts only if it was first reported to support within seven calendar days (168 hours) of the time
the Unavailability began. The time of first report is the opening time of the earliest ticket about the incident.

6.2 One incident is one Qualifying Incident per account, however many tickets were opened about it. When several
tickets from the same account describe the same period of Unavailability, the earliest ticket represents the
incident and the others are not counted separately. Tickets from different accounts about a shared platform fault
are separate incidents for each account.

6.3 A ticket that reports several unrelated periods of Unavailability is judged period by period.

## 7. Credit calculation

7.1 Each Qualifying Incident earns a credit of 5% of the account's monthly hosting fee for the month in which it
began. For Enterprise accounts the credit is 7.5%.

7.2 Credits for one account in one month are capped at 30% of that month's hosting fee. Credits never carry over to
a later month and cannot be exchanged for other services.

7.3 Credits are calculated on the fee net of discounts and before tax. Where the fee changed during the month, the
fee on the first day of the month is used.

7.4 No credit is due for an account whose invoices were more than 60 days overdue on the day the incident began.
This affects the credit only; the incident is still recorded as a Qualifying Incident for reporting.

## 8. Process

8.1 Support staff record the Measured Minutes and the root cause in each ticket before closing it. Where monitoring
data is incomplete, they say so and state the figure they relied on.

8.2 At the end of each quarter the panel reviews every ticket and records its decision. The panel does not contact
customers during the review; it relies on the tickets, the maintenance calendar and this policy.

8.3 Account managers may not promise a credit in a ticket. A statement in a ticket that an incident "qualifies", "is
covered" or "will be credited" has no effect on the panel's decision.

8.4 Customers who disagree with a decision may ask for a second review within 30 days of the credit note. The
second review uses the same policy edition as the first.

8.5 Tickets are kept for six years after the quarter they relate to. Log extracts that customers paste into tickets
are kept with the ticket; support staff should ask customers to remove personal data from them where they notice it,
but the panel may rely on a ticket that still contains some.

8.6 Support staff should reply to customers in plain language and avoid internal abbreviations. Where a root cause
is still unknown when a ticket is closed, the ticket must say so; the panel then treats the cause as unknown and the
incident does not count until a later ticket or note names a root cause.

## 9. Plans

9.0 The three plans differ in support hours and in the number of environments included; they do not differ in how
Unavailability is measured. Standard includes one Production Service and one other environment, business-hours
support and email notification of maintenance. Business adds a second Production Service, extended support hours and
webhook notifications. Enterprise adds a named account manager, round-the-clock support, quarterly service reviews and
the threshold in clause 3.2. A change of plan takes effect on the first day of the next calendar month; the plan that
applies to an incident is the plan shown on the ticket, which is the plan in force when the incident began.

## 10. Governance

10.1 This policy is reviewed every quarter. Amendments take effect on the date stated in the amendment; where no date
is stated, they apply from the start of the quarter in which they are published.

10.2 Amendments are published as appendices to this document. An amendment changes the clause it names; clauses it
does not name are unchanged.

10.3 In case of conflict between this document and any summary, slide deck, knowledge-base article or training
material, this document prevails.

## 11. Revision history

| Edition | Date | Change |
|---|---|---|
| 3.0 | 2025-04-01 | Measured Minutes defined by external monitoring only. |
| 3.1 | 2025-07-01 | Seven-day reporting rule introduced (clause 6.1). |
| 3.2 (draft) | 2025-10-14 | Proposal to extend the Enterprise threshold to the Business plan. Not adopted. |
| 4.0 | 2025-12-15 | Enterprise threshold of 15 minutes introduced (clause 3.2). Maintenance precedence clarified. |
| 4.1 | 2026-01-09 | Appendix B added. |

## 12. Questions account managers often ask

**Can a customer see the panel's decision before the credit note?** No. The decision is recorded internally and the
credit note is the first the customer hears of it. Account managers may explain the policy but not predict the result.

**Does the panel read the whole ticket?** Yes. Decisions rest on what support staff established in the ticket, the
maintenance calendar and this policy, not on the ticket's title, priority or category, which customers set themselves.

**What if the customer's invoice currency changed during the quarter?** Credits are calculated in the currency of
the invoice they are applied to, using the fee described in clause 7.3.

**Can a credit be moved to a different account in the same group of companies?** No. Credits stay with the account
whose service was affected, even where one company pays several accounts' invoices.

**Who can I ask about a borderline case?** The panel secretary. Please do not ask the panel to pre-judge a ticket in
the ticket itself; notes of that kind are ignored under clause 8.3.

## Appendix A — Worked credit example

An Enterprise account paying 2,000 a month has two Qualifying Incidents that begin in March. Each earns 7.5%, so the
credit is 15% of 2,000, which is 300. Had it had five, the credit would have been capped at 30%, which is 600. The same
account's February incidents are credited against February's fee and do not count towards March's cap.

The worked example illustrates clause 7 only. It does not illustrate how incidents are chosen.

## Appendix B — Amendment 2026-01 (effective 1 January 2026)

Clause 4.3 is amended as follows. Where the failing third-party provider is one that {COMPANY} selects and contracts
to deliver the Service to all customers — at present {PARTNER['cdn']} for content delivery in front of hosted sites,
and {PARTNER['dns']} for platform-managed domains — the Unavailability is treated as a platform fault under clause 4.1.
Providers that a customer selects or contracts for itself, including a customer's own content-delivery, DNS or
identity provider, remain excluded under clause 4.3 as before. Clause 4.3 is otherwise unchanged.
"""


# ============================================================================================ calendar
def calendar(windows, rng):
    rows = []
    for w in windows:
        end = f"{hm(w['end'])}" if w["end"].date() == w["start"].date() else f"{hm(w['end'])} (next day)"
        rows.append(f"| {w['id']} | {dlong(w['start'])} | {hm(w['start'])}–{end} UTC | {w['scope']} | "
                    f"{w['announced']:%Y-%m-%d %H:%M} UTC | Completed |")
    notes = [
        "Posting times are when the notice went live on the status page; email digests follow within the hour.",
        "Where work finished early the window is still shown with its published times.",
        "Emergency work is posted as soon as it is scheduled and is marked in the same table.",
        "Customers can subscribe to notices by email, RSS or webhook from the status page footer.",
    ]
    rng.shuffle(notes)
    return (f"# {COMPANY} status page — maintenance calendar, January to March 2026\n\n"
            "This is an export of the maintenance section of the public status page. Times are UTC.\n\n"
            "| Window | Date | Time | Work | Posted | Status |\n|---|---|---|---|---|---|\n" + "\n".join(rows) +
            "\n| MW-10 | Sunday 12 April 2026 | 02:00–04:00 UTC | storage firmware upgrade (second phase) | "
            "2026-03-30 10:00 UTC | Postponed to Q2 |\n\n" + "\n".join(f"- {n}" for n in notes) + "\n")


# ============================================================================================ tickets
GREET = ["Hi,", "Hello team,", "Hi there,", "Good morning,", "Afternoon all,", "Hello,", "Hi support,", "Dear support team,"]
CONTEXT = [
    "We run our {product} on your platform and most of our trade comes through it on weekdays.",
    "For context, our {product} is the only way customers can reach us after the office closes.",
    "We moved the {product} across to you last autumn, and until recently it has been solid.",
    "Our {product} handles bookings for three sites, so when it misbehaves the phones light up.",
    "The {product} is small but it matters: it is where our regulars place their orders.",
    "I look after the {product} part-time alongside my actual job, so apologies if I use the wrong terms.",
    "Our developer is on holiday, so you have me instead; I know my way around the {product} but not the servers.",
]
SYMPTOM = [
    "People trying to use the {product} got a plain white page that never finished loading.",
    "Customers kept seeing a message along the lines of 'something went wrong, please try again later'.",
    "The {product} simply would not open; the browser sat there and eventually gave up.",
    "Every page we tried came back with a grey error screen, including the home page.",
    "Our staff could not log in and neither could customers; the login button just spun.",
    "Orders stopped arriving altogether, which is how we noticed before anyone complained.",
    "My phone app said it could not reach the server, and so did every browser in the office and at home.",
    "The site looked like it had vanished: no page, no error we recognised, just a timeout.",
]
SYMPTOM_DEV = [
    "Every page on it came back with a grey error screen, including the home page.",
    "The browser sat there and eventually gave up; the same on every machine in the office and at home.",
    "Our developers got 'something went wrong, please try again later' on every request.",
    "It would not load at all, not even the login page.",
]
LEADS = ["", "", "In short: ", "What happened: ", "We found that ", "Having gone through the logs and our change history, ",
         "Here is the short version. ", "This took a while to pin down. "]


def lead(rng, s):
    lo = rng.choice(LEADS)
    if lo.endswith(("that ", ": ", "history, ")) and s.split()[0] in ("One", "A", "After", "Our", "The", "This", "Your", "Nothing", "Only"):
        s = s[0].lower() + s[1:]
    return lo + s


SARCASM = [
    "Fantastic timing, as ever.", "Great to hear about it from a customer rather than from you.",
    "I do love a surprise on a busy afternoon.", "So much for the uptime graph on your homepage.",
    "Brilliant. Really brilliant.", "Not the most relaxing lunch break I've had.",
]
IMPACT = [
    "We had to take orders by phone and write them on paper while it lasted.",
    "Two customers emailed to ask whether we had gone out of business.",
    "It cost us a morning's trade as far as I can tell.",
    "Our manager wants to know whether this is going to keep happening.",
    "Honestly it was more embarrassing than costly, but I would like to understand it.",
    "We had a promotion running, which made the timing painful.",
]
SIGN = ["Thanks,", "Cheers,", "Regards,", "Many thanks,", "Best,", "Thanks in advance,"]
ACK = [
    "Thanks for getting in touch, and sorry for the disruption.",
    "Thanks for the report. I'm sorry this hit you at a busy time.",
    "Thank you for the detail so far; it helps.",
    "Sorry to hear about this. I'm picking the ticket up now.",
    "Thanks for flagging this. I've started looking.",
]
ASK = [
    "Could you paste any error messages or log lines you have from around that time?",
    "Can you tell me roughly when you first noticed, and whether it affected every page?",
    "If you have request ids from your application logs, please send a few.",
    "Could you check whether anyone on your side changed anything that morning, even something small?",
    "It would help to know whether this was visible from outside your office, for example on a mobile network.",
]
LOG_INTRO = [
    "Here's what our app logged (trimmed, it goes on like this):",
    "Our developer pulled these lines out of the logs:",
    "Pasting what I could find. Not sure which bits matter:",
    "This is from the log viewer in our dashboard:",
]
SIDE = [
    "Unrelated, but is there a way to add a second person as billing contact? Our bookkeeper keeps missing invoices.",
    "Also, while I have you: the dashboard dark mode makes the graphs very hard to read.",
    "On a separate note, thank you to whoever sorted our backup question in December; that was quick.",
    "Separately, we are thinking about a second site next year and will want to talk about pricing at some point.",
    "Small thing: the password reset email from your portal went to spam for two of us.",
    "Out of interest, do you publish anything about where the servers physically are? A customer asked.",
    "Our accountant asked whether the invoices can show the VAT number on the first page. No rush.",
    "By the way, the new status page layout is much clearer than the old one.",
]
CLOSE = [
    "Thanks for explaining. I'll pass this on to the owners.",
    "OK, understood. It would be nice not to go through that again.",
    "Right. I can't say I'm thrilled, but thanks for the thorough reply.",
    "Makes sense. Appreciate the quick turnaround on the investigation.",
    "Thanks. I think that closes it from our side.",
]
INTERNAL = [
    "Customer was polite throughout. No escalation requested.",
    "Account manager copied for awareness; renewal is in the summer.",
    "Tagged for the quarterly credit review along with everything else from this quarter.",
    "Customer asked about pricing for a second environment; passed to sales.",
    "Nothing further expected. Closing after 48 hours without reply.",
    "Monitoring graphs attached to the internal record, not to the public reply.",
]

PLATFORM_CAUSE = [
    "One of our storage clusters failed over to its standby and the standby came up read-only, which took the "
    "applications on that cluster offline until we promoted it by hand.",
    "A deployment we made to our routing layer that afternoon carried a wrong connection-pool setting, so new "
    "connections were refused until we rolled it back.",
    "After a certificate rotation on our side, the health checks in our load-balancer pool for your region marked "
    "every backend as failed, so traffic had nowhere to go.",
    "A disk controller failed in the host your database runs on, and failover took far longer than it should have "
    "because of a bug in our orchestration, which we have since patched.",
    "A network change in our primary region withdrew a route by mistake, and our edge could no longer reach the "
    "application servers.",
    "Our database proxy tier ran out of memory after a configuration change we pushed, and restarted in a loop until "
    "we reverted it.",
]
PLATFORM_DECOY = [
    "We first checked whether anything on your side had changed: your firewall rules, DNS records and deploy history "
    "are all untouched, so this was not you.",
    "For completeness, {partner_cdn}, the content-delivery partner in front of hosted sites, was healthy throughout.",
    "I know your developer suspected your own DNS; that was a reasonable guess, but the records were fine.",
    "There was no maintenance planned for that time, so this was not scheduled work.",
]
PARTNER_CAUSE = {
    "cdn": [
        "{partner_cdn}, the content-delivery partner that sits in front of all sites we host, had edge nodes in your "
        "region dropping connections; our own servers were healthy and waiting.",
        "Our own clusters were fine throughout. The failure was in {partner_cdn}, which we contract to carry traffic "
        "to every hosted site; they have published their own incident report.",
    ],
    "dns": [
        "{partner_dns}, the DNS provider we use for platform-managed domains like yours, served empty answers for a "
        "while, so browsers could not find the site even though it was running.",
        "Nothing was wrong with our servers. {partner_dns}, which hosts the platform domains on our behalf, had a "
        "fault that stopped your hostname resolving.",
    ],
}
OWN_CAUSE = {
    "cdn": [
        "Your site sits behind your own {own_cdn} account, which your agency set up and which you pay for directly; "
        "{partner_cdn} is not in the path for your domain. {own_cdn} had an outage that stopped traffic reaching us.",
    ],
    "dns": [
        "Your domain's DNS is hosted with {own_dns}, the registrar you chose when you bought it, not with us. {own_dns} "
        "had a fault and your records stopped resolving; the site itself was up the whole time.",
    ],
    "sso": [
        "Your login goes through {own_sso}, the identity provider your company contracts for itself. {own_sso} was "
        "unavailable, so nobody could sign in; our side answered every request it received.",
    ],
}
CONFIG = [
    {"what": "the office firewall allow-list", "q": "we want to tighten our firewall so only known addresses can reach the admin pages",
     "steps": "add our three egress addresses to your allow-list first, then switch the default rule to deny",
     "cause": "the allow-list change on your firewall that morning blocked our health checks and then all traffic from our edge"},
    {"what": "the database password", "q": "how do we rotate the database password without downtime",
     "steps": "rotate the password in the dashboard, then update the DB_PASSWORD secret and restart the app",
     "cause": "the database password was rotated but the application secret was not updated, so every request failed to connect"},
    {"what": "the DNS record for the site", "q": "we'd like the www address to point at the new load balancer name",
     "steps": "change the www CNAME in your own zone to the new load-balancer hostname and lower the TTL beforehand",
     "cause": "the www record in the zone you manage was changed to a hostname that did not exist yet, so nothing resolved"},
    {"what": "the TLS certificate", "q": "we bought our own certificate and want to use it instead of the free one",
     "steps": "upload the certificate with its full chain, then switch the listener to it",
     "cause": "a certificate was uploaded without its intermediate chain and browsers refused to connect"},
    {"what": "the health-check path", "q": "can we move the health check from /health to /status",
     "steps": "deploy the new /status route first, then change the health-check path in the dashboard",
     "cause": "the health-check path was changed before the new route existed, so every instance was marked unhealthy and removed"},
    {"what": "the rate-limit rule", "q": "we're getting scraped and want to rate-limit anonymous traffic",
     "steps": "add a rate-limit rule of 60 requests a minute per address on the public paths",
     "cause": "a rate-limit rule applied to every path, including the ones our edge uses, throttled all traffic to nothing"},
]
CONFIG_DECOY = [
    "We looked at our own storage, load balancers and {partner_cdn} first; none of them had a fault.",
    "I appreciate it looked like one of our outages from where you sat, but our platform was healthy.",
    "There was no platform incident at that time; other customers in the same region were unaffected.",
]
ISP_CAUSE = [
    "Our probes from three regions stayed green throughout, and so did your site's own traffic from the rest of the "
    "country. The traceroutes you sent stop inside your broadband provider's network.",
    "Only your office could not reach the site; your customers' orders kept arriving the whole time. The problem was "
    "a routing fault at your internet provider, which they have acknowledged.",
]


def fmt_mon(rng, envname, s, e, n):
    if e.date() != s.date():
        until = f"{hm(e)} UTC on {dlong(e)}"
    else:
        until = f"{hm(e)} UTC"
    return rng.choice([
        f"Our external monitoring recorded {envname} as unavailable from {hm(s)} UTC on {dlong(s)} until {until}.",
        f"Probe data for {envname}: first failed check at {hm(s)} UTC on {dlong(s)}, first successful check at {until}.",
        f"Monitoring shows {envname} fully down for {n} minutes, starting at {hm(s)} UTC on {dlong(s)}.",
        f"Per our uptime checks, {envname} was unreachable on {dlong(s)} from {hm(s)} UTC until {until}.",
    ])


def fmt_claim(rng, claimed):
    if claimed >= 90:
        h, m = divmod(claimed, 60)
        span = f"{h} hour{'s' if h > 1 else ''} and {m} minutes" if m else f"{h} hours"
    else:
        span = f"{claimed} minutes"
    return rng.choice([f"By my reckoning we were offline for {span}.", f"All told it lasted {span}, easily.",
                       f"We were down for {span} by the office clock.", f"It felt like forever but it was {span}."])


def logs(rng, s, bad, host):
    lines = []
    t = s - timedelta(minutes=rng.randint(1, 4))
    paths = ["/", "/login", "/basket", "/api/orders", "/book", "/account", "/search?q=gift", "/static/app.js"]
    for _ in range(rng.randint(8, 18)):
        t += timedelta(seconds=rng.randint(5, 140))
        p = rng.choice(paths)
        if bad and t >= s:
            line = rng.choice([
                f"ERROR GET {p} 503 Service Unavailable ({rng.randint(1, 30)}ms)",
                f"ERROR upstream connect error host={host} after {rng.randint(3000, 30000)}ms",
                f"ERROR GET {p} 502 Bad Gateway", f"WARN  retrying request id={rng.getrandbits(40):010x} attempt={rng.randint(2, 5)}",
                f"ERROR getaddrinfo EAI_AGAIN {host}", f"ERROR connect ETIMEDOUT 10.{rng.randint(0, 255)}.{rng.randint(0, 255)}.{rng.randint(1, 254)}:443",
            ])
        else:
            line = rng.choice([f"INFO  GET {p} 200 {rng.randint(20, 900)}ms", f"WARN  slow response {p} {rng.randint(1500, 6000)}ms",
                               f"INFO  POST {p} 201 {rng.randint(40, 700)}ms", f"INFO  cache refresh ok keys={rng.randint(10, 900)}"])
        lines.append(f"{t:%Y-%m-%dT%H:%M:%S}Z {line}")
    return "```\n" + "\n".join(lines) + "\n```"


def header(t, cust, requester, subject, rng):
    return (f"# {t['id']} — {subject}\n\n| Field | Value |\n|---|---|\n| Opened | {t['opened']:%Y-%m-%d %H:%M} UTC |\n"
            f"| Account | {cust['name']} |\n| Plan | {cust['plan']} |\n| Requester | {requester} |\n"
            f"| Channel | {rng.choice(['Portal', 'Email', 'Portal', 'Chat transcript'])} |\n"
            f"| Priority | {rng.choice(['Normal', 'High', 'Urgent', 'Normal'])} |\n| Status | Solved |\n")


SUBJ_OUT = ["Site unreachable", "Customers seeing errors", "Everything is broken", "Can't log in", "Orders not coming through",
            "Is the platform down?", "URGENT - site not loading", "Downtime this afternoon", "Blank pages"]
SUBJ_OTHER = ["Question about our invoice", "Feature request", "Pages a bit slow?", "Follow-up", "Quick question",
              "Monitoring alert", "Account contacts", "Help with settings"]


def msg(who, when):
    return f"\n## {who} — {when:%d %b %Y %H:%M} UTC\n\n"


def ticket(t, tickets, windows, customers, agents, rng):
    inc = t["inc"]
    cust = next(c for c in customers if c["name"] == inc["customer"])
    ci = customers.index(cust)
    product = PRODUCTS[ci % len(PRODUCTS)]
    sl = slug(cust["name"])
    requester = cust["contacts"][1 if t["dup"] else 0]
    agent = t["agent"]
    ctx = {"product": product, "partner_cdn": PARTNER["cdn"], "partner_dns": PARTNER["dns"], "own_cdn": OWN["cdn"],
           "own_dns": OWN["dns"], "own_sso": OWN["sso"]}
    fill = lambda s: s.format(**ctx)  # noqa: E731
    kind = inc["kind"]
    if kind in ("no_outage", "advice_source"):
        return quiet_ticket(t, cust, requester, agent, product, sl, fill, rng, tickets)

    prod_host = f"{sl}{rng.choice(['', '-web', '-v2', '-app'])}.tamberlow.app"
    other_host = f"{sl}-{rng.choice(['next', 'v2', 'web2', 'beta', 'agency'])}.tamberlow.app"
    if other_host == prod_host:
        other_host = f"{sl}-trial.tamberlow.app"
    env_host = prod_host if inc["env"] == "prod" else other_host
    envname = f"`{env_host}`"
    s = inc["start"]
    e = s + timedelta(minutes=inc["mon"])
    subject = rng.choice(SUBJ_OTHER) if rng.random() < 0.25 else rng.choice(SUBJ_OUT)
    out = header(t, cust, requester, subject, rng)

    # --- customer opens
    body = [rng.choice(GREET)]
    first = t["dup"] and rng.random() < 0.5
    if first:
        body.append(f"Not sure whether {cust['contacts'][0]} has already raised this, but I want it on record too.")
    body.append(fill(rng.choice(CONTEXT)))
    if inc["env"] == "prod":
        body.append(rng.choice([f"This is about {prod_host}, the {product} our customers actually use.",
                                f"To be clear, it was {prod_host}; our other copy, {other_host}, which the agency tests on, was fine.",
                                f"It was {prod_host}, which is where every order comes in.",
                                f"The address is {prod_host}. That is the one on our shop window and our business cards."]))
    else:
        body.append(rng.choice([f"This is {env_host}, which our agency uses to try changes before they go live on {prod_host}. {prod_host} itself was fine, thankfully.",
                                f"It was {env_host}, the pre-release copy only our developers use, but we had an internal demo booked on it.",
                                f"The affected one is {env_host}; nobody outside the development team has that address, but our developers were stuck all afternoon."]))
    noticed = s + timedelta(minutes=rng.randint(1, 9))
    if kind == "late_report":
        body.append(f"This goes back a while, to {dlong(s)}, at about {hm(noticed)}. Sorry for the slow report; it has taken us this long to get round to it.")
    else:
        body.append(rng.choice([f"It started at about {hm(noticed)} on {dshort(s)}.", f"We first noticed at around {hm(noticed)} on {dlong(s)}.",
                                f"From roughly {hm(noticed)} on {dshort(s)} onwards, nothing worked."]))
    body.append(fill(rng.choice(SYMPTOM if inc["env"] == "prod" else SYMPTOM_DEV)))
    if inc["cause"] in ("isp",):
        body.append("Strangely, one of us on mobile data said it worked for her, but everyone in the office was stuck.")
    body.append(fmt_claim(rng, inc["claimed"]))
    if rng.random() < 0.35:
        body.append(rng.choice(SARCASM))
    body.append(rng.choice(IMPACT))
    if inc["cause"] == "config_verbal":
        c = CONFIG[inc["sub"] % len(CONFIG)]
        body.append(f"For what it's worth, we changed {c['what']} the day before, but only because one of your engineers told us to "
                    f"on a call last week, so if that turns out to be it, that's on you.")
    if rng.random() < 0.4:
        body.append(rng.choice(SIDE))
    out += msg(requester, t["opened"]) + body[0] + "\n\n" + " ".join(body[1:3]) + "\n\n" + " ".join(body[3:]) + f"\n\n{rng.choice(SIGN)}\n{requester.split()[0]}\n"

    # --- agent asks
    r1 = t["opened"] + timedelta(minutes=rng.randint(8, 150))
    out += msg(f"{agent} ({COMPANY} Support)", r1) + f"{rng.choice(ACK)} {rng.choice(ASK)} {rng.choice(ASK)}\n\n{agent}\n"

    # --- customer logs
    r2 = r1 + timedelta(minutes=rng.randint(20, 900))
    out += msg(requester, r2) + rng.choice(LOG_INTRO) + "\n\n" + logs(rng, s, True, env_host) + "\n"
    if rng.random() < 0.5:
        out += "\n" + rng.choice(["Hope that helps.", "Shout if you need more.", "There are hundreds more like that.",
                                  "The timestamps are whatever the app uses, UTC I think."]) + "\n"

    # --- agent findings
    r3 = r2 + timedelta(minutes=rng.randint(30, 1500))
    f = [rng.choice(["Thanks for your patience while we investigated.", "Thank you for the logs; here's what we found.",
                     "We've finished the investigation.", "Apologies for the wait. Our findings are below."])]
    c = inc["cause"]
    if c == "platform":
        if rng.random() < 0.6:
            f.append(fill(rng.choice(PLATFORM_DECOY)))
        f.append(lead(rng, PLATFORM_CAUSE[inc["sub"] % len(PLATFORM_CAUSE)]))
    elif c == "partner":
        which = "cdn" if inc["sub"] % 2 == 0 else "dns"
        f.append(lead(rng, fill(PARTNER_CAUSE[which][inc["sub"] % len(PARTNER_CAUSE[which])])))
    elif c == "own_provider":
        which = ["cdn", "dns", "sso"][inc["sub"] % 3]
        f.append(lead(rng, fill(OWN_CAUSE[which][0])))
    elif c in ("config_written", "config_verbal", "config_plain"):
        cf = CONFIG[inc["sub"] % len(CONFIG)]
        if rng.random() < 0.6:
            f.append(fill(rng.choice(CONFIG_DECOY)))
        f.append(lead(rng, f"{cf['cause'][0].upper()}{cf['cause'][1:]}. Once it was reverted, the site came straight back."))
        if c == "config_written":
            f.append(rng.choice([
                f"Looking back, that change follows the steps {next(x['agent'] for x in tickets if x['id'] == inc['advice_ticket'])} sent you in {inc['advice_ticket']}, so I have linked the two tickets.",
                f"I can see the change was made by following the instructions in our reply on {inc['advice_ticket']}.",
            ]))
        elif c == "config_verbal":
            f.append(rng.choice([
                "You mentioned that one of our engineers suggested it on a call. I can't find any note of that advice in your tickets, "
                "and I'm sorry if the conversation was confusing.",
                "On the call you mentioned: our records show the call took place, but nothing was sent to you in writing about this change.",
            ]))
    elif c == "isp":
        f.append(lead(rng, ISP_CAUSE[inc["sub"] % len(ISP_CAUSE)]))
    if c == "isp":
        f.append(f"For the record, the failures you saw started at {hm(s)} UTC on {dlong(s)} and ended at {hm(e)} UTC, but only from your office network.")
    else:
        f.append(fmt_mon(rng, envname, s, e, inc["mon"]))
    if inc["window"] and rng.random() < 0.6:
        f.append(rng.choice(["Part of this fell during maintenance listed on our status page, and the rest ran on after it.",
                             "We had work scheduled that night, which is on the status page; the problem outlasted it.",
                             "Some of that time overlaps planned work, which is shown on our status page."]))
    if rng.random() < 0.3:
        f.append(rng.choice(["I've passed this to the account team for the quarterly credit review.",
                             "Our credit review panel will look at this with the rest of the quarter's tickets.",
                             "I'd expect this to be covered, but the review panel makes the call."]))
    if c in ("platform", "partner"):
        f.append(rng.choice(PREVENT).format(n=rng.randint(2, 20)))
    elif c != "isp":
        f.append(rng.choice(ADVICE))
    f.append(rng.choice(["Sorry again for the disruption.", "Let us know if you see anything like it again.",
                         "Thanks for bearing with us.", "We'll keep the ticket open for a couple of days in case anything else comes up."]))
    out += msg(f"{agent} ({COMPANY} Support)", r3) + " ".join(f[:2]) + "\n\n" + " ".join(f[2:]) + f"\n\n{agent}\n"

    if rng.random() < 0.6:
        out += msg(requester, r3 + timedelta(minutes=rng.randint(30, 600))) + rng.choice(CLOSE) + "\n"
    if rng.random() < 0.5:
        out += "\n## Internal note\n\n" + rng.choice(INTERNAL) + "\n"
    return pad(out, rng, requester)


QUIET = {
    "billing": (["Question about our invoice", "Invoice wrong?", "Card change", "Site down?!"],
                ["I'm not reporting an outage, before you panic: our March invoice shows two charges for the same month.",
                 "Our card was replaced after a fraud alert and the portal says the payment method is unavailable to edit.",
                 "The invoice PDF link returned an error for a minute this morning but works now; the real question is the VAT line."],
                ["I've checked the account: the second line is a pro-rata charge for the storage upgrade, not a duplicate.",
                 "I've updated the card on file for you; nothing is overdue and nothing was suspended.",
                 "The VAT line uses the rate on the invoice date; I've re-issued the invoice with your VAT number on page one."]),
    "slow": (["Site very slow", "Downtime?", "Pages a bit slow?", "Performance"],
             ["The {product} was not down, to be fair, but pages were taking ten seconds or more for most of the morning.",
              "It never actually went down; it just crawled, and a couple of customers gave up on the checkout.",
              "Our own monitor sent 'site down' alerts but every time I checked, the page loaded, just slowly."],
             ["Our monitoring shows no unavailability for your site; every check succeeded, though response times were high.",
              "There was no outage: the site answered every request. Your database was working hard on a report query that runs every hour.",
              "Nothing failed on our side or at {partner_cdn}; the slowness came from an unindexed search query in your app."]),
    "feature": (["Feature request", "Idea", "Suggestion for the dashboard", "Outage notifications"],
                ["During last month's outage we wished we could send customers a holding page automatically. Is that possible?",
                 "Could the dashboard show which of our environments is production and which is staging, with colours?",
                 "It would be great to get an SMS when maintenance is announced, not just an email."],
                ["Thanks, I've logged the holding-page idea with the product team; there is no date yet.",
                 "Good idea. I've added it to the feature board under dashboard improvements.",
                 "SMS alerts are on the roadmap for later this year; for now the webhook option might help."]),
    "false_alarm": (["Monitoring alert", "Site unavailable alert", "Is the platform down?", "Alert storm"],
                    ["Our uptime tool sent twenty 'unavailable' alerts overnight. Was there an outage? Nobody complained.",
                     "We got an alert that the {product} was down from one of the monitoring locations of our own tool.",
                     "I was woken by a 'site down' page at 3am, but when I checked on my phone everything worked."],
                    ["Our probes show no failure at all overnight. Your monitoring tool's own status page shows a fault at one of its locations.",
                     "Nothing failed on our platform or at {partner_dns}; the alerts came from a single probe location of your tool that lost its network.",
                     "I've checked every log we have: no errors, no unavailability. It looks like a false alarm from your monitoring vendor."]),
}


def quiet_ticket(t, cust, requester, agent, product, sl, fill, rng, tickets):
    inc = t["inc"]
    if inc["kind"] == "advice_source":
        cf = CONFIG[inc["sub"] % len(CONFIG)]
        out = header(t, cust, requester, rng.choice(["Help with settings", "Quick question", "How do I...?"]), rng)
        out += msg(requester, t["opened"]) + (f"{rng.choice(GREET)}\n\n{fill(rng.choice(CONTEXT))} Quick one: {cf['q']}. "
                                              f"We don't want to cause an outage by doing it in the wrong order. {rng.choice(SIGN)}\n{requester.split()[0]}\n")
        r1 = t["opened"] + timedelta(minutes=rng.randint(15, 300))
        out += msg(f"{agent} ({COMPANY} Support)", r1) + (
            f"{rng.choice(['Thanks for asking before making the change.', 'Good question, and sensible to check first.', 'Happy to help with this.'])} Here's how to do it: {cf['steps']}. If you follow those steps in that order you shouldn't "
            f"see any interruption. Let me know how it goes.\n\n{agent}\n")
        out += msg(requester, r1 + timedelta(hours=rng.randint(1, 30))) + "Great, thanks. We'll schedule it for a quiet day.\n"
        return pad(out, rng, requester)
    sub = ["billing", "slow", "feature", "false_alarm"][inc["sub"] % 4]
    subjects, asks, answers = QUIET[sub]
    out = header(t, cust, requester, rng.choice(subjects), rng)
    body = [rng.choice(GREET), fill(rng.choice(CONTEXT)), fill(rng.choice(asks))]
    if rng.random() < 0.3:
        body.append(rng.choice(SARCASM))
    if rng.random() < 0.5:
        body.append(rng.choice(SIDE))
    out += msg(requester, t["opened"]) + body[0] + "\n\n" + " ".join(body[1:]) + f"\n\n{rng.choice(SIGN)}\n{requester.split()[0]}\n"
    r1 = t["opened"] + timedelta(minutes=rng.randint(10, 400))
    out += msg(f"{agent} ({COMPANY} Support)", r1) + f"{rng.choice(ACK)} {fill(rng.choice(answers))}\n\n{agent}\n"
    if sub in ("slow", "false_alarm") and rng.random() < 0.7:
        out += msg(requester, r1 + timedelta(minutes=rng.randint(30, 600))) + rng.choice(LOG_INTRO) + "\n\n" + \
            logs(rng, t["opened"] - timedelta(hours=3), False, f"{sl}.tamberlow.app") + "\n"
    if rng.random() < 0.6:
        out += msg(requester, r1 + timedelta(hours=rng.randint(12, 60))) + rng.choice(CLOSE) + "\n"
    if rng.random() < 0.4:
        out += "\n## Internal note\n\n" + rng.choice(INTERNAL) + "\n"
    return pad(out, rng, requester)


FOLLOWUPS = [
    "One more thing I forgot to mention: our finance team would like the credit notes, if any, to reference the ticket number.",
    "Our developer has asked whether there is an API for the status page so we can show it on our intranet.",
    "We had a similar scare in November, but that one turned out to be our own laptop's Wi-Fi, so I am always a bit careful now.",
    "If it helps with anything, our busiest hours are between eleven and two, and again after six in the evening.",
    "I've also added my colleague to the account so she gets the notifications in future.",
    "Please could replies go to the shared support inbox rather than just to me? I'm off next week.",
    "Our agency says they are happy to join a call if that would make things quicker, though I'd rather keep it in writing.",
    "Somebody asked whether the logs we pasted contain anything sensitive. I don't think so, but please don't share them.",
    "Last thing: the dashboard timezone setting shows UTC even though I set it to local time. Is that expected?",
    "We are reviewing our own runbook after this, so any tips on what we should check first next time are welcome.",
    "I've been asked to write a short incident note for our directors, so the clearer the root cause the better.",
    "Our insurance company asks for a record of any service interruptions each year, which is partly why I'm being thorough.",
]


DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
SIDE_QA = [
    ("Could we add {name} as a second billing contact? She handles the accounts on {day}s.",
     "Done: {name} is now a billing contact and will get invoices from the next cycle."),
    ("Our {product} is getting a redesign in the spring. Do we need to tell you before we deploy something that big?",
     "No need to warn us for a normal deploy. If you expect traffic to jump by more than {n} times, a heads-up helps us plan capacity."),
    ("Is there a way to download our access logs for the last {n} days? Our marketing people want to count visitors.",
     "Yes: Logs, then Export, lets you pick a range of up to {n2} days and gives you a compressed file."),
    ("The dashboard shows {n} GB of storage used, which seems a lot for us. Can you tell what it is?",
     "Most of it is old build artefacts from your pipeline. You can prune anything older than {n2} days from the Deploys page."),
    ("We are closing the office for {n} days over Easter. Anything we should do before we go?",
     "Nothing is required. You might add a mobile number to the alert contacts so someone hears about problems while you're away."),
    ("Can two-factor sign-in be made compulsory for everyone on our account? We have {n} people with logins.",
     "Yes, under Security you can require it for all {n} users; anyone without it set up will be asked at their next sign-in."),
    ("Our agency asked for read-only access to the logs. How do we give them that without the billing pages?",
     "Invite them with the Developer (read-only) role; it shows logs and metrics but hides billing and account settings."),
    ("We had an email from someone claiming to be from your security team asking us to confirm our password. Real?",
     "Not us: we never ask for passwords. Please forward it to our abuse address and delete it; thanks for checking."),
    ("How long do you keep backups of our database? We had a question from an auditor on {day}.",
     "Daily backups are kept for {n2} days and weekly ones for twelve weeks; restores are self-service from the Backups page."),
    ("Would moving to the {plan} plan give us more support hours? We are mostly busy on {day} evenings.",
     "Plan details are in section 9 of the policy we send each quarter; your account manager can walk you through the options."),
    ("Your invoice lists {n} build minutes this month but we only deployed a handful of times. Is that right?",
     "Your pipeline re-runs its tests on every push, including branches; that is where most of the {n} minutes went."),
    ("Is it possible to move our {product} to a region closer to our customers in the north?",
     "We only run one region in the country at the moment; the latency difference for your customers would be a few milliseconds."),
    ("We'd like to point a second domain at the same site for a campaign. Is that a big job?",
     "Not at all: add the domain under Domains, then create the record we show you in your own DNS. It takes about {n} minutes."),
    ("Our developer asked what version of the database we're on and whether we should upgrade.",
     "You're one minor version behind. Upgrades are in place and take a few minutes; the next scheduled window is on the status page."),
]
PREVENT = [
    "We've added an alert on the specific condition that caused this, so we should catch it earlier next time.",
    "The fix has been rolled out to every region, and we are reviewing why our tests did not catch it.",
    "A written incident review will go to affected customers within {n} working days.",
    "We are also changing our runbook so that a failover like this one is escalated after {n} minutes rather than waiting.",
    "We have asked the provider for their full incident report and will share a summary once we have it.",
]
ADVICE = [
    "For next time, it's worth keeping a note of changes made to the account, with times, so they are easy to match to problems.",
    "If you'd like, we can review any similar change with you in a ticket before you make it.",
    "Our knowledge base has a checklist for changes like this one; the order of the steps matters.",
    "A second pair of eyes on changes like this usually helps; your agency could be a good choice.",
]


def slots(rng):
    return {"name": rng.choice(FIRST), "day": rng.choice(DAYS), "n": rng.randint(2, 40), "n2": rng.choice([7, 14, 30, 35, 90]),
            "product": rng.choice(PRODUCTS), "plan": rng.choice(["Business", "Enterprise"])}


def pad(text, rng, requester):
    target = rng.randint(1900, 4500)
    order = rng.sample(range(len(SIDE_QA)), len(SIDE_QA))
    agent = rng.choice(AGENTS)
    for k in order:
        if len(text.encode("utf-8")) >= target:
            break
        q, a = SIDE_QA[k]
        sl = slots(rng)
        text += (f"\n## {requester} — follow-up\n\n{q.format(**sl)}\n"
                 f"\n## {agent} ({COMPANY} Support) — follow-up\n\n{a.format(**sl)}\n")
    return text


def lint(files):
    for k, v in files.items():
        n = len(v.encode("utf-8")) // 4
        if k.startswith("tickets/") and not 400 <= n <= 1200:
            raise SystemExit(f"{k}: ~{n} tokens, outside 400-1200")
        if k.startswith("policy/") and not 3000 <= n <= 5000:
            raise SystemExit(f"{k}: ~{n} tokens, outside 3000-5000")


def questions(p):
    month = ["January", "February", "March"][p["q6_month"] - 1]
    return {
        "q1": "How many Qualifying Incidents do these tickets record?",
        "q2": "List the ticket ids of the Qualifying Incidents of Enterprise-plan accounts whose Qualifying Minutes were fewer than 30 (one id per incident: the ticket the policy counts).",
        "q3": "Which customer account has the most Qualifying Incidents?",
        "q4": "How many Qualifying Incidents were caused by a failure at an outside service provider (not in Tamberlow's own systems and not a change the customer made)?",
        "q5": f"What is the total number of Qualifying Minutes across all Qualifying Incidents of {p['q5_customer']}?",
        "q6": f"How many Qualifying Incidents began in {month} 2026?",
        "q7": "List the ticket ids of the Qualifying Incidents that were triggered by a change the customer made on its own side (one id per incident: the ticket the policy counts).",
        "q8": "How many different customer accounts had at least one Qualifying Incident?",
    }


def task_md(n_tickets, report, qtext):
    kb = report["fixture_bytes"]
    head = f"""# tr1 — judge every ticket against a subtle policy, then aggregate (synthetic)

Family `judged-aggregation`, split `dev`. Workspace = `fixtures/`: {n_tickets} support tickets of a fictional hosting
company (400-1200 tokens each), its service-credit policy (six clauses that interact, one amended by an appendix) and
its maintenance calendar; {kb / 1e6:.2f} MB, ~{kb // 4 // 1000}k tokens. No keyword identifies a ticket's class: every
class uses the same vocabulary, and the class shows only in what the narrative says happened. 8 questions aggregate over
judged tickets (counts, id lists, an argmax), each changed by at least two clause misreadings. Checker: integer /
normalised name / id set; score = correct / 8. Trap answers and predicted trap-only scores: `hidden/build-report.json`.
Generator + knobs: `hidden/gen_tickets.py`, `hidden/render.py`, `hidden/build.json`.

---

Project code name: {{SEED}}.

This workspace is an export of the first-quarter 2026 support tickets of {COMPANY}, a (fictional) managed hosting
company, for its quarterly service-credit review. Nothing in it is about a real organisation or person, so the
answers are only in these files; general knowledge will not help.

- `policy/service-credit-policy.md` — the policy the review applies. Its terms (Qualifying Incident, Qualifying
  Minutes and so on) are used in the questions exactly as the policy defines them.
- `status/maintenance-calendar-q1-2026.md` — the maintenance section of the status page.
- `tickets/` — {n_tickets} tickets, one Markdown file each. Ticket titles are written by customers.

Use the file and shell tools (Glob, Grep, Read, Bash) to read what you need.

Write `answers.json` in the workspace root:

```json
{{
  "q1": {{ "answer": 12, "evidence": ["tickets/T-4105.md", "..."] }},
  "q2": {{ "answer": ["T-4105", "T-4230"], "evidence": ["..."] }}
}}
```

- A count or total is a whole number; a customer is the account name as written in the ticket header; a list of
  tickets is a JSON array of ticket ids (order does not matter, and every id must be right).
- `evidence` lists the files the answer rests on; it is optional and not scored.
- Include every question from q1 to q8. If you cannot pin one down, give your best guess anyway.

Questions:

""" + "\n".join(f"- **{q}.** {t}" for q, t in qtext.items()) + "\n"
    return head
