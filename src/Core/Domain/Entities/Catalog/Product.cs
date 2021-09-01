using DN.WebApi.Domain.Contracts;

namespace DN.WebApi.Domain.Entities.Catalog
{
    public class Product : AuditableEntity, IMustHaveTenant
    {
        public Product(string name, string description, decimal rate)
        {
            Name = name;
            Description = description;
            Rate = rate;
        }

        protected Product()
        {
        }

        public string Name { get; private set; }

        public string Description { get; private set; }

        public decimal Rate { get; private set; }

        public string TenantId { get; set; }
    }
}