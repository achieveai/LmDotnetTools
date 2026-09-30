# Tamberlow Hosting — Service Credit Policy for Managed Hosting

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

2.1 **Service.** The hosting of a customer's application or site on the Tamberlow Hosting platform, including the platform's
load balancers, compute, storage, databases and the routing that carries traffic to them.

2.2 **Production Service.** A customer environment that serves the customer's own users or customers. Staging,
test, preview, user-acceptance and sandbox environments are not Production Services, whatever they are called by
the customer and even when they share a plan with a Production Service. Unavailability of an environment that is
not a Production Service never produces a Qualifying Incident.

2.3 **Unavailability.** A period in which a Production Service cannot be reached, or returns errors for all
requests, from outside the customer's own network. Slow responses, partial errors, individual failing pages and
problems affecting a single user or office are not Unavailability.

2.4 **Measured Minutes.** The length of a period of Unavailability as recorded by Tamberlow Hosting's external monitoring,
from the first failed check to the first successful check afterwards, as stated by support staff in the ticket.
Estimates given by the customer, by the customer's own monitoring tools or by third parties are not used to measure
an incident, even when they are longer or more precise.

2.5 **Qualifying Minutes.** Measured Minutes after the deductions in clause 5.

2.6 **Qualifying Incident.** A period of Unavailability that meets clauses 3, 4 and 6. Each Qualifying Incident
earns one service credit under clause 7.

2.7 **Written Instruction.** Advice given by Tamberlow Hosting support staff in a reply to a support ticket. Advice given by
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

4.1 **Platform faults count.** Unavailability caused by a fault in the Tamberlow Hosting platform, or by a change that
Tamberlow Hosting made to it, counts towards a Qualifying Incident.

4.2 **Customer-side causes.** Unavailability caused by the customer does not count. This includes changes to the
customer's own firewall, allow-lists, credentials, certificates, DNS records that the customer manages, application
code, configuration or deployment pipeline, and suspension of an account for non-payment. It does count, however, if
the change that caused the Unavailability was made by following a Written Instruction; in that case the incident is
treated as a platform fault under clause 4.1. The panel must be able to find the Written Instruction in a ticket.

4.3 **Third-party providers.** Unavailability caused by the failure of a third-party provider, including
content-delivery networks, DNS providers, identity providers and payment services, is not a Tamberlow Hosting fault and does
not count.

4.4 **The customer's own connectivity.** Unavailability that is visible only from the customer's own offices or
networks, including failures inside the customer's internet service provider, is not Unavailability under clause
2.3 and does not count.

4.5 Where more than one cause contributed, the panel decides by the cause named in the ticket as the root cause
by support staff after investigation, not by the cause the customer first suspected.

## 5. Planned maintenance

5.1 Tamberlow Hosting carries out planned maintenance in windows published on the status page (the maintenance calendar).
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

Clause 4.3 is amended as follows. Where the failing third-party provider is one that Tamberlow Hosting selects and contracts
to deliver the Service to all customers — at present Veltrane Edge for content delivery in front of hosted sites,
and Orrisway DNS for platform-managed domains — the Unavailability is treated as a platform fault under clause 4.1.
Providers that a customer selects or contracts for itself, including a customer's own content-delivery, DNS or
identity provider, remain excluded under clause 4.3 as before. Clause 4.3 is otherwise unchanged.
