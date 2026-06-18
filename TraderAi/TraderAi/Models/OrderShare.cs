namespace TraderAi.Models;

public sealed class OrderShare
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int ShareId { get; set; }

    public Order? Order { get; set; }

    public Share? Share { get; set; }
}
