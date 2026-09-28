## v1.9.0 (minor)

Changes since v1.8.0:

- Filter the invalidated keys with Where ([@Claude](https://github.com/Claude))
- Discard the ignored Position value in FailingWriteStream ([@Claude](https://github.com/Claude))
- Invalidate every ref's lock snapshot when a relayed lock changes ([@Claude](https://github.com/Claude))
- Refuse to publish a staging file whose write failed ([@Claude](https://github.com/Claude))
- Gate Dependabot auto-merge on CI actually being green ([@Claude](https://github.com/Claude))
- refactor: split the lock routes into their own handler [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- fix: treat a lost publish race as the duplicate it is [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: adopt the consolidated .NET workflow [patch] ([@Claude](https://github.com/Claude))
- fix: guard staging files in the store rather than relying on the host [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: make the SonarQube quality gate opt in [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: adopt the unified dotnet workflow [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Reduce complexity in the tool entry point and the fan-out parser ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Clear the Sonar findings from the locks work ([@matt-edmondson](https://github.com/matt-edmondson))

