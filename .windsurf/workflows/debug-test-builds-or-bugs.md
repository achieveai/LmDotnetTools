---
description: Debug tests, builds or bugs using step by step approach
---

# Debugging Tests


When fixing failed test, it need to be done in step by step process using sequential thinking.

## Step 1:

Look at the failure, analyze the code and come up with Root Cause Assertion. Take a note of supporting evidence.

## Step 2:

Validate the assertion by adding logs to the code and re-running the tests case. Make sure the changes are only for diagnostic purpose.

## Step 3:

If Root Cause is validated, come up with design changes that may address the root cause. Use supporting evidence to check how these design changes will fix the issue. Use sequential thinking where ever necessary.

## Step 4:

Plan how to implement design changes.

## Step 5:

Execute the plan

## Step 6:

Validate test fixes. If tests are still broken go back to Step 1 or Step 3 based on if design was incorrect or root cause analysis was incorrect.