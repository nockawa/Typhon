# 🐍 Typhon 🐍

**A real-time, low latency and very fast ACID database.**

Documentation can be found [here](https://nockawa.github.io/Typhon/).

# History
This project went through many things:
- Bootstrapped in 2015 with a very different design and intent, then quickly put on a shelf.
- Resurrected during COVID in 2020 as a POC of "is it possible to make a real-time ACID database, down to the µs", oriented for persistent games ? Then put on a shelf after promising work.
- Many concepts around unsafe/GC-free .net programming lead me to develop [🍅](https://github.com/nockawa/Tomate), but the two projects are not dependent. I, for once, successfully restrained myself to retrofit 🍅 into this one, it's totally doable, but as usual, just a matter of time...
- Re-resurrected in summer 2025 with the "firm, but fragile" intention to reach an alpha stage.

## Why ?
Initially I wanted to "make a database engine for MMOs, something fast, reliable and scalable" (in that order).

Something like a weird mixed between [ECS](https://en.wikipedia.org/wiki/Entity_component_system) and a "regular database engine".

### Fast
In the realm of the micro-second. Concessions would have to be made, but it has to be fast, otherwise there's no really a point to it.

### More suitable
Not the original intent, but quickly a very interesting angle. Adopting some of the ECS principals would make this more natural for the users.

### Reliable, meaning Durable (or not...)
Atomic, transaction-based and durable operations. 

Through a design decision, the user can opt-out durability on chosen components.

### Scalable
While it's still a goal, the ambitions were tuned down. A theoretical evolution would be a shard/hash based implementation, but the resulting complexity makes this no longer an objective.
