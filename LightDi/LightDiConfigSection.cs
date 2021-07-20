using System.Configuration;

namespace LightDi
{
    public class LightDiConfigSection : ConfigurationSection
    {
        [ConfigurationProperty("", Options = ConfigurationPropertyOptions.IsDefaultCollection)]
        [ConfigurationCollection(typeof(ContainerRegistrationCollection), AddItemName = "register")]
        public ContainerRegistrationCollection Registrations
        {
            get => (ContainerRegistrationCollection) this[""];
            set => this[""] = value;
        }
    }
}