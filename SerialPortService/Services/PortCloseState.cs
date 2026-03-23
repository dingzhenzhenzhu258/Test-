namespace SerialPortService.Services
{
    public enum PortCloseState
    {
        NotStarted = 0,
        Running = 1,
        Completed = 2,
        TimedOut = 3,
        Faulted = 4
    }
}

