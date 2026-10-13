# NetLift setup skill

[`netlift-native-app`](netlift-native-app/SKILL.md) helps a coding agent create
or connect native Kotlin/Android and Swift/iOS apps to shared .NET code.
It supports new or existing projects on either side, reviews the reachable
public API, suggests compatible facades, and verifies actual runtime results.

## Install

Copy the `netlift-native-app` folder into your agent's supported skills
directory. For a repository-local Copilot skill, run from the consuming
repository:

```bash
mkdir -p .github/skills/netlift-native-app
curl --fail --location \
  https://raw.githubusercontent.com/Redth/netlift-sdk/main/skills/netlift-native-app/SKILL.md \
  -o .github/skills/netlift-native-app/SKILL.md
```

Inspect the downloaded file before using it. If a skill with that name already
exists, compare it rather than overwriting local customizations. Other agents
can use the same standard `SKILL.md` in their supported skill location; no
EngrAIm installation or bundled executable is required.

## Example requests

- "Create a Kotlin/Compose app and a new .NET greeting library using NetLift."
- "Add an Android front end to this existing .NET library; keep its API intact."
- "Connect this existing Android app to our existing C# business logic."
- "Add a new .NET service to this existing Swift package; first verify client
  generation, then explain what is required for a real iOS host."
- "Review these public service interfaces for Kotlin/Swift projection and
  suggest narrow facade contracts without breaking existing consumers."

The skill is a setup workflow, not a claim that every mobile platform is
supported. It distinguishes in-memory Swift previews from C# execution and
must surface unsupported targets or missing hosting steps.

## Validation and maintenance

The skill passed its structural/safety gate before export. Its package pins
and commands were checked against public-feed Android `alpha.14` and Swift
`alpha.9` consumers. The README's new-project C#, Kotlin, and Swift examples
were extracted into a separate project and built; the Android app returned
the real managed greeting and the Swift preview returned its explicitly
in-memory greeting.

The alpha.14 rerun used empty Gradle and NuGet caches, then verified the
managed greeting and all ten signed R8 Orders acceptance markers twice across
process relaunch. Swift alpha.9 generation also matched the real source-owned
iOS workload host's contracts and passed its managed acceptance separately.

The recipe also covers the checked-in existing-project Orders consumers:
runtime-default clients, explicit injection, installation errors, callbacks,
streams, and cancellation. It is not an automated multi-agent benchmark;
recheck the canonical public docs and package metadata when updating it.
