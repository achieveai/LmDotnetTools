# Consumer expanded activity research

## Current path

- Consumer view groups reasoning, tool metadata, and internal notifications into `TurnActivity` while keeping user and assistant answers visible.
- The collapsed `TurnActivity` summary already reports working, completed, waiting, and failed states correctly.
- Expanding currently renders `MetadataPill` with its default `card` presentation. That restores the old boxed pill, its “Show all” header, and internal height cap.
- Developer view already passes `presentation="activity-row"` directly from `MessageList`; those rows are the accepted visual behavior and must remain unchanged.
- Notifications in both direct `MessageList` paths and expanded `TurnActivity` already use the updated `NotificationPill` component.

## Small change

- Pass the existing `presentation="activity-row"` prop from `TurnActivity` to `MetadataPill`.
- Keep grouping, collapsed summary, notification rendering, assistant detail rendering, result lookup, deferred questions, and agent controls unchanged.
- Update the existing Consumer expansion regression to require `activity-row`, proving that expanded Consumer details share the Developer row contract without changing Developer wiring.
