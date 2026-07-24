namespace OpenVerse.Tests;

// the shadow engine and EngineBoot are process-global singletons (one GameMgr, one match), so serialize every test
// that boots or drives them
[CollectionDefinition("Engine", DisableParallelization = true)]
public class EngineCollection { }
