# LightDi
A simple light weight dependency injection container

```var container = new ObjectContainer();
container.RegisterTypeAs<SomeClass, ISomeInterface>();
container.RegisterInstanceAs<IAnotherInterface>(new SomeClassWithConstructor  Status = some value );
var someClass = container.Resolve<ISomeInterface>();  
var obj = container.Resolve<IAnotherInterface>();

