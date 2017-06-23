using System.Threading;
using System.Threading.Tasks;

class LockMess
{
    private int state = 0;

    public async void Run()
    {
        while (true)
        {
            lock (this)
            {
                if (state > 0) break;
            }
            await Task.Delay(1000); // Как бы запрос к серверу
        }

        while (true)
        {
            bool b = false;
            lock (this)
            {
                if (state == 1)
                {
                    b = true;
                    state = 2;
                }
            }
            if (b) await Task.Delay(1000); // Как бы запрос к серверу

            while (true)
            {
                lock (this)
                {
                    if (state > 2) break;
                }
                await Task.Delay(1000); // Как бы запрос к серверу
            }

            lock (this)
            {
                while (state == 3) Monitor.Wait(this);
                state = 1;
            }
        }
    }

    public void Reply1()
    {
        lock(this)
        {
            if (state == 0) state = 1;
        }
    }

    public async Task Reply3()
    {
        lock (this)
        {
            if (state == 2) state = 3;
        }

        await Task.Delay(1000); // Как бы запрос к серверу

        lock (this)
        {
            if (state == 3) state = 4;
            Monitor.PulseAll(this);
        }
    }
}
