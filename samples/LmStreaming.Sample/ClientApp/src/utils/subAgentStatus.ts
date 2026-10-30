/**
 * Why a sub-agent ended the way it did, in words, or null when its status already says enough.
 * Shared by the agent tabs and the inspector so both explain an ending the same way.
 */
export function subAgentEndReason(status: string | null | undefined, failureCode: string | null | undefined): string | null {
  if (!failureCode) return null;
  if (status === 'error') return failureCode;
  // #792: a provider switch that rebuilt the parent stopped this child. A plain host shutdown needs no reason.
  if (status === 'stopped' && failureCode === 'parent_recreated') return 'parent provider changed';
  return null;
}
