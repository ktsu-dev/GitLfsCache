## v1.12.2 (patch)

Changes since v1.12.1:

- Hand an aborted leader's fetch to one follower instead of all of them [patch] ([@Claude](https://github.com/Claude))
- Store objects for an upstream key with a dot in it, such as gitlab.com [patch] ([@Claude](https://github.com/Claude))
- Expire a batch action's proxy token no later than upstream's href [patch] ([@Claude](https://github.com/Claude))
- Cover the --store override inside the command action ([@Claude](https://github.com/Claude))
- Answer 502, not 500, when upstream's 2xx batch body cannot be used [patch] ([@Claude](https://github.com/Claude))
- Resolve a relative --store path instead of aborting startup [patch] ([@Claude](https://github.com/Claude))
- Refuse a locks/batch ref that is not an object with 400 instead of 500 [patch] ([@Claude](https://github.com/Claude))

