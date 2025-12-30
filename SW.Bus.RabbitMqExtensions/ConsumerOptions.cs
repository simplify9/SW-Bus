namespace SW.Bus.RabbitMqExtensions;

public class ConsumerOptions
{
    public ushort? Prefetch { get; set; }
    public int? Priority { get; set; }
}