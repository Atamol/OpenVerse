namespace OpenVerse.Tests;

// ShadowBridge keeps the pair, pool and solo mirror in statics, so tests that boot or drive an engine stomp each other
[CollectionDefinition("Engine", DisableParallelization = true)]
public class EngineCollection { }
