using System.Configuration;

namespace LightDi
{
    public class ContainerRegistrationConfigElement : ConfigurationElement
    {
        [ConfigurationProperty("as", IsRequired = true)]
        public string Interface
        {
            get => (string) this["as"];
            set => this["as"] = value;
        }

        [ConfigurationProperty("type", IsRequired = true)]
        public string Implementation
        {
            get => (string) this["type"];
            set => this["type"] = value;
        }

        [ConfigurationProperty("name", IsRequired = false, DefaultValue = null)]
        public string Name
        {
            get => (string) this["name"];
            set => this["name"] = value;
        }
    }
}