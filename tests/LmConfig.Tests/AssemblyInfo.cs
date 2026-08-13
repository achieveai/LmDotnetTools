// LmConfig's tests exercise provider-availability logic, which resolves API keys from
// environment variables. Environment variables are process-global, but xUnit runs distinct
// test classes in parallel by default, so a writer in one class is visible to a reader in
// another mid-flight.
//
// That race was live: ConfigurationLoadingTests, Models/AppConfigTests and UnifiedAgentTests
// all write TEST_API_KEY, and Agents/ModelResolverTests writes six more keys
// (VALID_PROVIDER_API_KEY, PROVIDER1/2_API_KEY, VALID1/2_API_KEY, VALID_API_KEY).
// TestUtilities/LmConfigTestBuilder writes arbitrary names on behalf of any consumer.
// Each writer restores its variable in a finally block, which is correct and still
// insufficient — the cleanup is scoped to the writing test, not to the concurrent reader.
//
// The observed failure was ModelResolver_IsProviderAvailableAsync_WithoutApiKey_ShouldReturnFalse,
// whose whole premise is that TEST_API_KEY is unset; it asserted False while a sibling class
// had the variable set. It passed on main and failed on the very next run, which is the
// signature of a race rather than a regression.
//
// Serializing the assembly removes the interference structurally instead of per-variable:
// with no two classes in flight, no writer can overlap a foreign reader. The suite is 95
// tests running in ~18s, so the wall-clock cost is negligible.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
