using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using static System.String;

namespace LightDi
{
    public class ObjectContainer : IObjectContainer
    {

        private readonly ObjectContainer baseContainer;
        private readonly Dictionary<RegistrationKey, object> objectPool = new Dictionary<RegistrationKey, object>();

        private readonly ConcurrentDictionary<RegistrationKey, IRegistration> registrations =
            new ConcurrentDictionary<RegistrationKey, IRegistration>();

        private readonly List<RegistrationKey> resolvedKeys = new List<RegistrationKey>();

        private bool isDisposed;

        static ObjectContainer()
        {
            DisableThreadSafeResolution =
                !IsNullOrEmpty(Environment.GetEnvironmentVariable(Constants.DisableThreadSafeTypeResolution));
        }

        public ObjectContainer(IObjectContainer baseContainer = null)
        {
            if (baseContainer != null && !(baseContainer is ObjectContainer))
                throw new ArgumentException("Base container must be an ObjectContainer", nameof(baseContainer));

            this.baseContainer = (ObjectContainer) baseContainer;
            RegisterInstanceAs<IObjectContainer>(this);
        }

        public static bool DisableThreadSafeResolution { get; set; }
        public IObjectContainer BaseContainer => baseContainer;

        public static TimeSpan ConcurrentObjectResolutionTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public event Action<object> ObjectCreated;

        public void Dispose()
        {
            isDisposed = true;

            foreach (var obj in objectPool.Values.OfType<IDisposable>().Where(o => !ReferenceEquals(o, this)))
                obj.Dispose();

            objectPool.Clear();
            registrations.Clear();
            resolvedKeys.Clear();
        }

        public override string ToString()
        {
            return Join(Environment.NewLine,
                registrations
                    .Where(r => !(r.Value is NamedInstanceDictionaryRegistration))
                    .Select(r =>
                        $"{r.Key} -> {(r.Key.Type == typeof(IObjectContainer) && r.Key.Name == null ? "<self>" : r.Value.ToString())}"));
        }

        private void AssertNotDisposed()
        {
            if (isDisposed)
                throw new ObjectContainerException("Object container disposed", null);
        }

        /// <summary>
        ///     A simple immutable linked list of <see cref="Type" />.
        /// </summary>
        private class ResolutionList
        {
            private readonly RegistrationKey currentRegistrationKey;
            private readonly Type currentResolvedType;
            private readonly ResolutionList nextNode;

            public ResolutionList()
            {
                Debug.Assert(IsLast);
            }

            private ResolutionList(RegistrationKey currentRegistrationKey, Type currentResolvedType,
                ResolutionList nextNode)
            {
                this.currentRegistrationKey = currentRegistrationKey;
                this.currentResolvedType = currentResolvedType;
                this.nextNode = nextNode ?? throw new ArgumentNullException(nameof(nextNode));
            }

            private bool IsLast => nextNode == null;

            public ResolutionList AddToEnd(RegistrationKey registrationKey, Type resolvedType)
            {
                return new ResolutionList(registrationKey, resolvedType, this);
            }

            public bool Contains(Type resolvedType)
            {
                if (resolvedType == null) throw new ArgumentNullException(nameof(resolvedType));
                return GetReverseEnumerable().Any(i => i.Value == resolvedType);
            }

            public bool Contains(RegistrationKey registrationKey)
            {
                return GetReverseEnumerable().Any(i => i.Key.Equals(registrationKey));
            }

            private IEnumerable<KeyValuePair<RegistrationKey, Type>> GetReverseEnumerable()
            {
                var node = this;
                while (!node.IsLast)
                {
                    yield return new KeyValuePair<RegistrationKey, Type>(node.currentRegistrationKey,
                        node.currentResolvedType);
                    node = node.nextNode;
                }
            }

            public Type[] ToTypeList()
            {
                return GetReverseEnumerable().Select(i => i.Value ?? i.Key.Type).Reverse().ToArray();
            }

            public override string ToString()
            {
                return Join(",", GetReverseEnumerable().Select(n => $"{n.Key}:{n.Value}"));
            }
        }

        private struct RegistrationKey
        {
            public readonly Type Type;
            public readonly string Name;

            public RegistrationKey(Type type, string name)
            {
                Type = type ?? throw new ArgumentNullException(nameof(type));
                Name = name;
            }

            private Type TypeGroup
            {
                get
                {
                    if (Type.IsGenericType && !Type.IsGenericTypeDefinition)
                        return Type.GetGenericTypeDefinition();
                    return Type;
                }
            }

            public override string ToString()
            {
                Debug.Assert(Type.FullName != null);
                if (Name == null)
                    return Type.FullName;

                return $"{Type.FullName}('{Name}')";
            }

            private bool Equals(RegistrationKey other)
            {
                var canInvert = other.TypeGroup == Type || other.Type == TypeGroup || other.Type == Type;
                return canInvert && String.Equals(other.Name, Name, StringComparison.CurrentCultureIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is RegistrationKey && Equals((RegistrationKey) obj);
            }

            public override int GetHashCode()
            {
                return TypeGroup.GetHashCode();
            }
        }

        #region Registration types

        private enum SolvingStrategy
        {
            PerContext,
            PerDependency
        }

        private interface IRegistration
        {
            object Resolve(ObjectContainer container, RegistrationKey keyToResolve, ResolutionList resolutionPath);
        }

        private class TypeRegistration : RegistrationWithStrategy, IRegistration
        {
            private readonly Type implementationType;
            private readonly object syncRoot = new object();

            public TypeRegistration(Type implementationType)
            {
                this.implementationType = implementationType;
            }

            protected override object ResolvePerContext(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                var typeToConstruct = GetTypeToConstruct(keyToResolve);

                var pooledObjectKey = new RegistrationKey(typeToConstruct, keyToResolve.Name);

                var result = ExecuteWithLock(syncRoot, () => container.GetPooledObject(pooledObjectKey), () =>
                {
                    if (typeToConstruct.IsInterface)
                        throw new ObjectContainerException("Interface cannot be resolved: " + keyToResolve,
                            resolutionPath.ToTypeList());

                    var obj = container.CreateObject(typeToConstruct, resolutionPath, keyToResolve);
                    container.objectPool.Add(pooledObjectKey, obj);
                    return obj;
                }, resolutionPath);

                return result;
            }


            protected override object ResolvePerDependency(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                var typeToConstruct = GetTypeToConstruct(keyToResolve);
                if (typeToConstruct.IsInterface)
                    throw new ObjectContainerException("Interface cannot be resolved: " + keyToResolve,
                        resolutionPath.ToTypeList());
                return container.CreateObject(typeToConstruct, resolutionPath, keyToResolve);
            }

            private Type GetTypeToConstruct(RegistrationKey keyToResolve)
            {
                var targetType = implementationType;
                if (!targetType.IsGenericTypeDefinition) return targetType;
                var typeArgs = keyToResolve.Type.GetGenericArguments();
                targetType = targetType.MakeGenericType(typeArgs);

                return targetType;
            }

            public override string ToString()
            {
                return "Type: " + implementationType.FullName;
            }
        }

        private class InstanceRegistration : IRegistration
        {
            private readonly object instance;

            public InstanceRegistration(object instance)
            {
                this.instance = instance;
            }

            public object Resolve(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                return instance;
            }

            public override string ToString()
            {
                string instanceText;
                try
                {
                    instanceText = instance.ToString();
                }
                catch (Exception ex)
                {
                    instanceText = ex.Message;
                }

                return "Instance: " + instanceText;
            }
        }

        private abstract class RegistrationWithStrategy : IResolverStrategyRegistration
        {
            protected SolvingStrategy solvingStrategy = SolvingStrategy.PerContext;

            public IResolverStrategyRegistration InstancePerDependency()
            {
                solvingStrategy = SolvingStrategy.PerDependency;
                return this;
            }

            public IResolverStrategyRegistration InstancePerContext()
            {
                solvingStrategy = SolvingStrategy.PerContext;
                return this;
            }

            public virtual object Resolve(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                return solvingStrategy == SolvingStrategy.PerDependency ? ResolvePerDependency(container, keyToResolve, resolutionPath) : ResolvePerContext(container, keyToResolve, resolutionPath);
            }

            protected abstract object ResolvePerContext(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath);

            protected abstract object ResolvePerDependency(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath);

            protected static object ExecuteWithLock(object lockObject, Func<object> getter, Func<object> factory,
                ResolutionList resolutionPath)
            {
                var obj = getter();

                if (obj != null)
                    return obj;

                if (DisableThreadSafeResolution)
                    return factory();

                if (Monitor.TryEnter(lockObject, ConcurrentObjectResolutionTimeout))
                    try
                    {
                        obj = getter();

                        if (obj == null)
                        {
                        }
                        else
                            return obj;

                        obj = factory();
                        return obj;
                    }
                    finally
                    {
                        Monitor.Exit(lockObject);
                    }

                throw new ObjectContainerException(
                    "Concurrent object resolution timeout (potential circular dependency).",
                    resolutionPath.ToTypeList());
            }
        }

        private class FactoryRegistration : RegistrationWithStrategy, IRegistration{
            private readonly Delegate factoryDelegate;
            private readonly object syncRoot = new object();

            public FactoryRegistration(Delegate factoryDelegate)
            {
                this.factoryDelegate = factoryDelegate;
            }

            protected override object ResolvePerContext(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                var result = ExecuteWithLock(syncRoot, () => container.GetPooledObject(keyToResolve), () =>
                {
                    var obj = container.InvokeFactoryDelegate(factoryDelegate, resolutionPath, keyToResolve);
                    container.objectPool.Add(keyToResolve, obj);
                    return obj;
                }, resolutionPath);

                return result;
            }

            protected override object ResolvePerDependency(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                return container.InvokeFactoryDelegate(factoryDelegate, resolutionPath, keyToResolve);
            }
        }

        private class NonDisposableWrapper
        {
            public NonDisposableWrapper(object obj)
            {
                Object = obj;
            }

            public object Object { get; }
        }

        private class NamedInstanceDictionaryRegistration : IRegistration
        {
            public object Resolve(ObjectContainer container, RegistrationKey keyToResolve,
                ResolutionList resolutionPath)
            {
                var typeToResolve = keyToResolve.Type;
                Debug.Assert(typeToResolve.IsGenericType &&
                             typeToResolve.GetGenericTypeDefinition() == typeof(IDictionary<,>));

                var genericArguments = typeToResolve.GetGenericArguments();
                var keyType = genericArguments[0];
                var targetType = genericArguments[1];
                var result =
                    (IDictionary) Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(genericArguments));

                foreach (var namedRegistration in container.registrations
                    .Where(r => r.Key.Name != null && r.Key.Type == targetType).Select(r => r.Key).ToList())
                {
                    var convertedKey = ChangeType(namedRegistration.Name, keyType);
                    Debug.Assert(convertedKey != null);
                    result.Add(convertedKey, container.Resolve(namedRegistration.Type, namedRegistration.Name));
                }

                return result;
            }

            private object ChangeType(string name, Type keyType)
            {
                if (keyType.IsEnum)
                    return Enum.Parse(keyType, name, true);

                Debug.Assert(keyType == typeof(string));
                return name;
            }
        }

        #endregion

        #region Registration

        public IResolverStrategyRegistration RegisterTypeAs<TInterface>(Type implementationType, string name = null)
            where TInterface : class
        {
            var interfaceType = typeof(TInterface);
            return RegisterTypeAs(implementationType, interfaceType, name);
        }

        public IResolverStrategyRegistration RegisterTypeAs<TType, TInterface>(string name = null)
            where TType : class, TInterface
        {
            var interfaceType = typeof(TInterface);
            var implementationType = typeof(TType);
            return RegisterTypeAs(implementationType, interfaceType, name);
        }

        public IResolverStrategyRegistration RegisterTypeAs(Type implementationType, Type interfaceType)
        {
            if (!IsValidTypeMapping(implementationType, interfaceType))
                throw new InvalidOperationException("type mapping is not valid");
            return RegisterTypeAs(implementationType, interfaceType, null);
        }

        private bool IsValidTypeMapping(Type implementationType, Type interfaceType)
        {
            if (interfaceType.IsAssignableFrom(implementationType))
                return true;

            if (interfaceType.IsGenericTypeDefinition && implementationType.IsGenericTypeDefinition)
            {
                var baseTypes = GetBaseTypes(implementationType).ToArray();
                return baseTypes.Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == interfaceType);
            }

            return false;
        }

        private static IEnumerable<Type> GetBaseTypes(Type type)
        {
            if (type.BaseType == null) return type.GetInterfaces();

            return Enumerable.Repeat(type.BaseType, 1)
                .Concat(type.GetInterfaces())
                .Concat(type.GetInterfaces().SelectMany(GetBaseTypes))
                .Concat(GetBaseTypes(type.BaseType));
        }


        private RegistrationKey CreateNamedInstanceDictionaryKey(Type targetType)
        {
            return new RegistrationKey(typeof(IDictionary<,>).MakeGenericType(typeof(string), targetType), null);
        }

        private void AddRegistration(RegistrationKey key, IRegistration registration)
        {
            registrations[key] = registration;

            AddNamedDictionaryRegistration(key);
        }

        private IRegistration EnsureImplicitRegistration(RegistrationKey key)
        {
            var registration =
                registrations.GetOrAdd(key, registrationKey => new TypeRegistration(registrationKey.Type));

            AddNamedDictionaryRegistration(key);

            return registration;
        }

        private void AddNamedDictionaryRegistration(RegistrationKey key)
        {
            if (key.Name != null)
            {
                var dictKey = CreateNamedInstanceDictionaryKey(key.Type);
                registrations.TryAdd(dictKey, new NamedInstanceDictionaryRegistration());
            }
        }

        private IResolverStrategyRegistration RegisterTypeAs(Type implementationType, Type interfaceType, string name)
        {
            var registrationKey = new RegistrationKey(interfaceType, name);
            AssertNotResolved(registrationKey);

            ClearRegistrations(registrationKey);
            var typeRegistration = new TypeRegistration(implementationType);
            AddRegistration(registrationKey, typeRegistration);

            return typeRegistration;
        }

        public void RegisterInstanceAs(object instance, Type interfaceType, string name = null, bool dispose = false)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));
            var registrationKey = new RegistrationKey(interfaceType, name);
            AssertNotResolved(registrationKey);

            ClearRegistrations(registrationKey);
            AddRegistration(registrationKey, new InstanceRegistration(instance));
            objectPool[new RegistrationKey(instance.GetType(), name)] = GetPooledInstance(instance, dispose);
        }

        private static object GetPooledInstance(object instance, bool dispose)
        {
            return instance is IDisposable && !dispose ? new NonDisposableWrapper(instance) : instance;
        }

        public void RegisterInstanceAs<TInterface>(TInterface instance, string name = null, bool dispose = false)
            where TInterface : class
        {
            RegisterInstanceAs(instance, typeof(TInterface), name, dispose);
        }

        public IResolverStrategyRegistration RegisterFactoryAs<TInterface>(Func<TInterface> factoryDelegate, string name = null)
        {
            return RegisterFactoryAs(factoryDelegate, typeof(TInterface), name);
        }

        public IResolverStrategyRegistration RegisterFactoryAs<TInterface>(Func<IObjectContainer, TInterface> factoryDelegate,
            string name = null)
        {
            return RegisterFactoryAs(factoryDelegate, typeof(TInterface), name);
        }

        public void RegisterFactoryAs<TInterface>(Delegate factoryDelegate, string name = null)
        {
            RegisterFactoryAs(factoryDelegate, typeof(TInterface), name);
        }

        public IResolverStrategyRegistration RegisterFactoryAs(Delegate factoryDelegate, Type interfaceType, string name = null)
        {
            if (factoryDelegate == null) throw new ArgumentNullException(nameof(factoryDelegate));
            if (interfaceType == null) throw new ArgumentNullException(nameof(interfaceType));

            var registrationKey = new RegistrationKey(interfaceType, name);
            AssertNotResolved(registrationKey);

            ClearRegistrations(registrationKey);
            var factoryRegistration = new FactoryRegistration(factoryDelegate);
            AddRegistration(registrationKey, factoryRegistration);

            return factoryRegistration;
        }

        public bool IsRegistered<T>()
        {
            return IsRegistered<T>(null);
        }

        public bool IsRegistered<T>(string name)
        {
            var typeToResolve = typeof(T);

            var keyToResolve = new RegistrationKey(typeToResolve, name);

            return registrations.ContainsKey(keyToResolve);
        }

        // ReSharper disable once UnusedParameter.Local
        private void AssertNotResolved(RegistrationKey interfaceType)
        {
            if (resolvedKeys.Contains(interfaceType))
                throw new ObjectContainerException("An object has been resolved for this interface already.", null);
        }

        private void ClearRegistrations(RegistrationKey registrationKey)
        {
            registrations.TryRemove(registrationKey, out var result);
        }
        
        public void RegisterFromConfiguration()
        {
            var section = (LightDiConfigSection) ConfigurationManager.GetSection("boDi");
            if (section == null)
                return;

            RegisterFromConfiguration(section.Registrations);
        }

        public void RegisterFromConfiguration(ContainerRegistrationCollection containerRegistrationCollection)
        {
            if (containerRegistrationCollection == null)
                return;

            foreach (ContainerRegistrationConfigElement registrationConfigElement in containerRegistrationCollection)
                RegisterFromConfiguration(registrationConfigElement);
        }

        private void RegisterFromConfiguration(ContainerRegistrationConfigElement registrationConfigElement)
        {
            var interfaceType = Type.GetType(registrationConfigElement.Interface, true);
            var implementationType = Type.GetType(registrationConfigElement.Implementation, true);

            RegisterTypeAs(implementationType, interfaceType,
                IsNullOrEmpty(registrationConfigElement.Name) ? null : registrationConfigElement.Name);
        }
        #endregion

        #region Resolve

        public T Resolve<T>()
        {
            return Resolve<T>(null);
        }

        public T Resolve<T>(string name)
        {
            var typeToResolve = typeof(T);

            var resolvedObject = Resolve(typeToResolve, name);

            return (T) resolvedObject;
        }

        public object Resolve(Type typeToResolve, string name = null)
        {
            return Resolve(typeToResolve, new ResolutionList(), name);
        }

        public IEnumerable<T> ResolveAll<T>() where T : class
        {
            return registrations
                .Where(x => x.Key.Type == typeof(T))
                .Select(x => Resolve(x.Key.Type, x.Key.Name) as T);
        }

        private object Resolve(Type typeToResolve, ResolutionList resolutionPath, string name)
        {
            AssertNotDisposed();

            var keyToResolve = new RegistrationKey(typeToResolve, name);
            var resolvedObject = ResolveObject(keyToResolve, resolutionPath);
            if (!resolvedKeys.Contains(keyToResolve)) resolvedKeys.Add(keyToResolve);
            Debug.Assert(typeToResolve.IsInstanceOfType(resolvedObject));
            return resolvedObject;
        }

        private KeyValuePair<ObjectContainer, IRegistration>? GetRegistrationResult(RegistrationKey keyToResolve)
        {
            IRegistration registration;
            if (registrations.TryGetValue(keyToResolve, out registration))
                return new KeyValuePair<ObjectContainer, IRegistration>(this, registration);

            if (baseContainer != null)
                return baseContainer.GetRegistrationResult(keyToResolve);

            if (IsSpecialNamedInstanceDictionaryKey(keyToResolve))
            {
                var targetType = keyToResolve.Type.GetGenericArguments()[1];
                return GetRegistrationResult(CreateNamedInstanceDictionaryKey(targetType));
            }

            if (IsDefaultNamedInstanceDictionaryKey(keyToResolve))
                return new KeyValuePair<ObjectContainer, IRegistration>(this,
                    new NamedInstanceDictionaryRegistration());

            return null;
        }

        private bool IsDefaultNamedInstanceDictionaryKey(RegistrationKey keyToResolve)
        {
            return IsNamedInstanceDictionaryKey(keyToResolve) &&
                   keyToResolve.Type.GetGenericArguments()[0] == typeof(string);
        }

        private bool IsSpecialNamedInstanceDictionaryKey(RegistrationKey keyToResolve)
        {
            return IsNamedInstanceDictionaryKey(keyToResolve) &&
                   keyToResolve.Type.GetGenericArguments()[0].IsEnum;
        }

        private bool IsNamedInstanceDictionaryKey(RegistrationKey keyToResolve)
        {
            return keyToResolve.Name == null && keyToResolve.Type.IsGenericType &&
                   keyToResolve.Type.GetGenericTypeDefinition() == typeof(IDictionary<,>);
        }

        private object GetPooledObject(RegistrationKey pooledObjectKey)
        {
            object obj;
            return GetObjectFromPool(pooledObjectKey, out obj) ? obj : null;
        }

        private bool GetObjectFromPool(RegistrationKey pooledObjectKey, out object obj)
        {
            if (!objectPool.TryGetValue(pooledObjectKey, out obj))
                return false;

            if (obj is NonDisposableWrapper nonDisposableWrapper)
                obj = nonDisposableWrapper.Object;

            return true;
        }

        private object ResolveObject(RegistrationKey keyToResolve, ResolutionList resolutionPath)
        {
            if (keyToResolve.Type.IsPrimitive || keyToResolve.Type == typeof(string) || keyToResolve.Type.IsValueType)
                throw new ObjectContainerException(
                    "Primitive types or structs cannot be resolved: " + keyToResolve.Type.FullName,
                    resolutionPath.ToTypeList());

            var registrationResult = GetRegistrationResult(keyToResolve);

            var registrationToUse = registrationResult ??
                                    new KeyValuePair<ObjectContainer, IRegistration>(this,
                                        EnsureImplicitRegistration(keyToResolve));

            var resolutionPathForResolve = registrationToUse.Key == this ? resolutionPath : new ResolutionList();
            var result = registrationToUse.Value.Resolve(registrationToUse.Key, keyToResolve, resolutionPathForResolve);

            return result;
        }


        private object CreateObject(Type type, ResolutionList resolutionPath, RegistrationKey keyToResolve)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                constructors = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);

            Debug.Assert(constructors.Length > 0, "Class must have a constructor!");

            var maxParamCount = constructors.Max(ctor => ctor.GetParameters().Length);
            var maxParamCountCtors = constructors.Where(ctor => ctor.GetParameters().Length == maxParamCount).ToArray();

            object obj;
            if (maxParamCountCtors.Length == 1)
            {
                var ctor = maxParamCountCtors[0];
                if (resolutionPath.Contains(keyToResolve))
                    throw new ObjectContainerException("Circular dependency found! " + type.FullName,
                        resolutionPath.ToTypeList());

                var args = ResolveArguments(ctor.GetParameters(), keyToResolve,
                    resolutionPath.AddToEnd(keyToResolve, type));
                obj = ctor.Invoke(args);
            }
            else
            {
                throw new ObjectContainerException(
                    "Multiple public constructors with same maximum parameter count are not supported! " +
                    type.FullName, resolutionPath.ToTypeList());
            }

            OnObjectCreated(obj);

            return obj;
        }

        protected virtual void OnObjectCreated(object obj)
        {
            var eventHandler = ObjectCreated;
            eventHandler?.Invoke(obj);
        }

        private object InvokeFactoryDelegate(Delegate factoryDelegate, ResolutionList resolutionPath,
            RegistrationKey keyToResolve)
        {
            if (resolutionPath.Contains(keyToResolve))
                throw new ObjectContainerException("Circular dependency found! " + factoryDelegate,
                    resolutionPath.ToTypeList());

            var args = ResolveArguments(factoryDelegate.Method.GetParameters(), keyToResolve,
                resolutionPath.AddToEnd(keyToResolve, null));
            return factoryDelegate.DynamicInvoke(args);
        }

        private object[] ResolveArguments(IEnumerable<ParameterInfo> parameters, RegistrationKey keyToResolve,
            ResolutionList resolutionPath)
        {
            return parameters.Select(p =>
                IsRegisteredNameParameter(p)
                    ? ResolveRegisteredName(keyToResolve)
                    : Resolve(p.ParameterType, resolutionPath, null)).ToArray();
        }

        private object ResolveRegisteredName(RegistrationKey keyToResolve)
        {
            return keyToResolve.Name;
        }

        private bool IsRegisteredNameParameter(ParameterInfo parameterInfo)
        {
            return parameterInfo.ParameterType == typeof(string) &&
                   parameterInfo.Name.Equals(Constants.RegisteredNameParameterName);
        }

        #endregion
    }
}