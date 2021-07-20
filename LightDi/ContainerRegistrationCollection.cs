using System.Configuration;

namespace LightDi
{
    public class ContainerRegistrationCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new ContainerRegistrationConfigElement();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            var registrationConfigElement = (ContainerRegistrationConfigElement) element;
            var elementKey = registrationConfigElement.Interface;
            if (registrationConfigElement.Name != null)
                elementKey = elementKey + "/" + registrationConfigElement.Name;
            return elementKey;
        }

        public void Add(string implementationType, string interfaceType, string name = null)
        {
            BaseAdd(new ContainerRegistrationConfigElement
            {
                Implementation = implementationType,
                Interface = interfaceType,
                Name = name
            });
        }
    }
}