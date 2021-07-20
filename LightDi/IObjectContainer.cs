using System;
using System.Collections.Generic;

namespace LightDi
{
    public interface IObjectContainer : IDisposable
    {
        /// <summary>
        ///     Triggers when a new object is created directly by the container
        /// </summary>
        event Action<object> ObjectCreated;

        /// <summary>
        ///     Register a type as the implementation type of an interface.
        /// </summary>
        IResolverStrategyRegistration RegisterTypeAs<TType, TInterface>(string name = null) where TType : class, TInterface;

        /// <summary>
        ///     Register an instance
        /// </summary>
        void RegisterInstanceAs<TInterface>(TInterface instance, string name = null, bool dispose = false)
            where TInterface : class;

        /// <summary>
        ///     Register an instance
        /// </summary>
       
        void RegisterInstanceAs(object instance, Type interfaceType, string name = null, bool dispose = false);

        /// <summary>
        ///     Registers an instance produced by <paramref name="factoryDelegate" />. The delegate will be called only once and
        ///     the instance it returned will be returned in each resolution.
        /// </summary>
       
        IResolverStrategyRegistration RegisterFactoryAs<TInterface>(Func<IObjectContainer, TInterface> factoryDelegate,
            string name = null);

        /// <summary>
        ///     Resolves an implementation object for an interface or type.
        /// </summary>
        T Resolve<T>();

        /// <summary>
        ///     Resolves an implementation object for an interface or type.
        /// </summary>
        T Resolve<T>(string name);

        /// <summary>
        ///     Resolves an implementation object for an interface or type.
        /// </summary>
        object Resolve(Type typeToResolve, string name = null);

        /// <summary>
        ///     Resolves all implementations of an interface or type.
        /// </summary>
        IEnumerable<T> ResolveAll<T>() where T : class;

        /// <summary>
        ///     Determines whether the interface or type is registered.
        /// </summary>
       bool IsRegistered<T>();

        /// <summary>
        ///     Determines whether the interface or type is registered with the specified name.
        /// </summary>
          bool IsRegistered<T>(string name);
    }
}