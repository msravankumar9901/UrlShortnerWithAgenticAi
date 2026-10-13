# Engineering principles

These are the rules for this repository.

1. The LLM is not the workflow controller. `WorkflowEngine` owns status, order, retries, approvals, rollback, and replan.
2. Agents return structured results and may write only through the stage allow-list.
3. Security and review stages are read-only. Documentation writes docs paths. Tests write test paths and may run `dotnet test`. Implementation writes the run workspace, not an unrestricted tree.
4. High-impact stages (`HumanRequired`, `AutoWithReview`) record an approval. `--auto-approve` is an explicit demo flag, not a hidden default in the engine.
5. Do not invent missing business requirements. Record ambiguity and stop for a human when security, privacy, or external behavior is unclear.
6. Do not delete or weaken a test to make a run pass.
7. Do not hardcode provider keys. Read `OPENAI_*` or `AZURE_OPENAI_*` from the environment.
8. Do not give an agent a production shell, a database admin connection, or a secret store.
9. Prefer a modular monolith. No extra services for their own sake.
10. Keep the prototype runnable after each phase: build, then tests.

## Definition of done

A change is done when the solution builds, the automated tests that cover it pass, the audit log can be verified for a scenario run, and the docs do not claim a behavior the code does not have.
