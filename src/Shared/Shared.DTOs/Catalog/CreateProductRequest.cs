namespace DN.WebApi.Shared.DTOs.Catalog
{
    public class CreateProductRequest : IMustBeValid
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal Rate { get; set; }
    }
}