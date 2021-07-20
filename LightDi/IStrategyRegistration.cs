namespace LightDi
{
    public interface IResolverStrategyRegistration
    {
        /// <summary>
        ///     Single instance per object container. Default
        /// </summary>
        /// <returns></returns>
        IResolverStrategyRegistration InstancePerContext();
        
        /// <summary>
        ///   New instance per object container.
        /// </summary>
        /// <returns></returns>
        IResolverStrategyRegistration InstancePerDependency();

        
    }
}