using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

var commandMode = false;
var garageStatus = "closed";

var builder = WebApplication.CreateBuilder(args);
var deterministic = Environment.GetEnvironmentVariable("SMARTER_GARAGE_TEST_MODE") == "1";
var useDevCertificate = Environment.GetEnvironmentVariable("SMARTER_GARAGE_USE_DEV_CERT") == "1";

var certFile = @"C:\temp\ssl\cert_key\certificate.pfx";
var certPW = "rowdster";

// Parse ports from command-line args or use defaults
int httpsPort = 3000;
int httpPort = 3001;

if (args.Length > 0 && int.TryParse(args[0], out var parsedHttps))
    httpsPort = parsedHttps;
if (args.Length > 1 && int.TryParse(args[1], out var parsedHttp))
    httpPort = parsedHttp;


builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpsPort, listenOptions =>
    {
        if (useDevCertificate) listenOptions.UseHttps();
        else listenOptions.UseHttps(new X509Certificate2(certFile, certPW));
    });
    if (deterministic) options.ListenLocalhost(httpPort);
});

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
if (deterministic) DeterministicSimulator.Install(app, httpPort);

var statuses = new[]
{
        "closed", "opening", "open", "closing"
};

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Urls.Add($"https://*:{httpsPort}");
app.Urls.Add($"http://*:{httpPort}");

int[] timings = new int[] {8,10,15};
//int[] timings = new int[] { 1, 1, 1 };

app.UseHttpsRedirection();

void RunKeyboardCommands()
{
    while(true)
    {
        var key = Console.ReadKey().KeyChar;
        switch (key)
        {
            case 'O':
                {
                    garageStatus = "open";
                    Console.WriteLine("OPEN", true);
                    break;
                }
            case 'o':
                {
                    garageStatus = "open";
                    RunCommand("OPEN");
                    break;
                }
            case 'C':
                {
                    garageStatus = "closed";
                    Console.WriteLine("CLOSE", true);
                    break;
                }
            case 'c':
                {
                    garageStatus = "closed";
                    RunCommand("CLOSE");
                    break;
                }
            case 'p':
                {
                    garageStatus = "opening";
                    break;
                }
            case 'l':
                {
                    garageStatus = "closing";
                    break;
                }
            case 'e':
            case 'E':
                {
                    Console.Clear();
                    break;
                }
            default:
                {
                    break;
                }
        }
    }


}

void RunCommand(string command, bool immediate = false)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nCommand received: " + command);
    Console.ResetColor();
    command = command.ToUpper();

    commandMode = true;
    if (command == "OPEN")
    {
        if (immediate)
        {
            garageStatus = "open";
            ShowUpdate(ConsoleColor.Green, String.Format("OPEN, wait {0} seconds", timings[0]));
            return;
        }
        garageStatus = "closed";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... closed, wait {0} seconds", timings[0]));
        Thread.Sleep(timings[0] * 1000);
        garageStatus = "opening";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... opening, wait {0} seconds", timings[1]));
        Thread.Sleep(timings[1] * 1000);
        garageStatus = "open";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... open, wait {0} seconds", timings[2]));
        Thread.Sleep(timings[2] * 1000);
    }
    if (command == "CLOSE")
    {
        if (immediate)
        {
            garageStatus = "CLOSED";
            ShowUpdate(ConsoleColor.Green, String.Format("CLOSED, wait {0} seconds", timings[0]));
            return;
        }

        garageStatus = "open";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... open,  wait {0} seconds", timings[0]));
        Thread.Sleep(timings[0] * 1000);
        garageStatus = "closing";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... closing, wait {0} seconds", timings[1]));
        Thread.Sleep(timings[1] * 1000);
        garageStatus = "closed";
        ShowUpdate(ConsoleColor.Green, String.Format("Running... closed, wait {0} seconds", timings[2]));
        Thread.Sleep(timings[2] * 1000); 
    }
    commandMode = false;

    static void ShowUpdate(ConsoleColor color, String message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}

app.MapPost("/v1/devices/{garageAccessKey}/commands", async (string garageAccessKey, HttpRequest request) =>
{
    // Thread.Sleep(4000);

    // { "results":[{ "id":"e2649c22-ccd7-4a72-81a4-fad8aa79632f","status":"ACCEPTED"}]}

    //if (commands[0].command == "OPEN" || commands[0].command == "CLOSE")
    //{
    //    commandMode = true;
    //    currentCommandMode = commands[0].command;
    //    Thread thread = new Thread(() => { RunCommand(currentCommandMode); });
    //    thread.Start();
    //}

    
    await using var memStream = new MemoryStream();
    await request.BodyReader.CopyToAsync(memStream);

    var reader = new StreamReader(memStream);
    memStream.Seek(0, SeekOrigin.Begin);

    string commandsJSON = reader.ReadToEnd();
    Commands commands = (Commands)JsonSerializer.Deserialize(commandsJSON, typeof(Commands), new JsonSerializerOptions { });

    string commandToRun = commands.commands[0].command;

    Thread commandProcessor = new Thread(async () => RunCommand(commandToRun));
    commandProcessor.Start();  

    var status = new { results = new[] { new { status = "ACCEPTED" } } };
    return status;
})
    .WithName("GarageCommands");

DateTime lastStatusCheck = DateTime.Now;

app.MapGet("/v1/devices", () =>
{
    var devices = new
    {
        items = new[]
        {
            new
            {
                deviceId = "56561c12-57e7-4c72-b931-3023bd3b1462",
                name = "base-garage-door",
                label = "Garage Door",
                manufacturerName = "SmartThingsCommunity",
                presentationId = "f",
                deviceManufacturerCode = "014F-0001",
                locationId = "9186aa44-d0fb-464f-afa6-28d11be2537f",
                ownerId = "40f95c82-393a-43ed-8f62-6f222e906f3e",
                roomId = "40f95c82-393a-43ed-8f62-6f222e906f3f",
                components = new[]
                {
                    new
                    {
                        id = "main",
                        label = "main",
                        capabilities = new[]
                        {
                            new { id = "doorControl", version = 1 },
                            new { id = "contactSensor", version = 1 },
                            new { id = "refresh", version = 1 }
                        },
                        categories = new[]
                        {
                            new { name = "GarageDoor", categoryType = "manufacturer" }
                        },
                        optional = false
                    }
                },
                createTime = "2023-08-14T00:45:03.174Z",
                parentDeviceId = "40f95c82-393a-43ed-8f62-6f222e906f3a",
                profile = new { id = "40f95c82-393a-43ed-8f62-6f222e906f3b" },
                zwave = new
                {
                    networkId = "0B",
                    driverId = "40f95c82-393a-43ed-8f62-6f222e906f3c",
                    executingLocally = true,
                    hubId = "",
                    networkSecurityLevel = "ZWAVE_S0_LEGACY",
                    provisioningState = "PROVISIONED",
                    manufacturerId = 335,
                    productType = 18244,
                    productId = 13616
                },
                type = "ZWAVE",
                restrictionTier = 0,
                allowed = Array.Empty<object>(),
                executionContext = "LOCAL",
                relationships = Array.Empty<object>()
            }
        },
        _links = new { }
    };

    return devices;

}).WithName("Devices");

app.MapGet("/v1/devices/{garageAccessKey}/status", (string garageAccessKey) =>
{
    //    var time = (int)((float)DateTime.Now.Second / (float)60 * 4);
    Object status;

    if (String.IsNullOrEmpty(garageAccessKey))
        garageAccessKey = "None";


    // Thread.Sleep(4000);

    Console.WriteLine($"Status request, status: {garageStatus} for: {garageAccessKey} - {(DateTime.Now - lastStatusCheck)}");
    lastStatusCheck = DateTime.Now;

    if (commandMode)
    {
        status = new { components = new { main = new { doorControl = new { door = new { value = garageStatus } } } } };
        return status;
    }

//    status = new { components = new { main = new { doorControl = new { door = new { value = statuses[time], time = time } } } } };
    status = new { components = new { main = new { doorControl = new { door = new { value = garageStatus} } } } };


    return status;
})
    .WithName("GarageStatus");

// Simple ping style POST endpoint to allow sender to share information
app.MapPost("/ping", async (HttpRequest request) =>
{

    await using var memStream = new MemoryStream();
    await request.BodyReader.CopyToAsync(memStream);

    var reader = new StreamReader(memStream);
    memStream.Seek(0, SeekOrigin.Begin);

    string message = reader.ReadToEnd();
    Console.WriteLine(String.IsNullOrEmpty(message) ? "Empty": $"Ping {message}");

    var response = new { };
    return response;
    
})
    .WithName("ping");

Thread keyboardCommands = new Thread(async () => RunKeyboardCommands());
if (!deterministic && !Console.IsInputRedirected) keyboardCommands.Start();

app.Run();



class Commands
{
    public Command[] commands { get; set; }
}
class Command
{
    public string? component { get; set; }
    public string? capability { get; set; }
    public string command { get; set; }

}
