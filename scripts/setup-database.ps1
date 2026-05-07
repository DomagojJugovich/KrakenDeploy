# Sets up the KrakenDeploy database (interactive prompts)
param(
    [string]$ConnectionString
)

if (-not $ConnectionString) {
    Write-Host "Database Setup" -ForegroundColor Cyan
    Write-Host "=============="
    Write-Host ""
    Write-Host "Recommended path: let the installer create the database for you."
    Write-Host ""

    $mode = Read-Host "Choose path: [1] Create new database   [2] I already have a connection string"
    if ($mode -eq '1') {
        $hostname   = Read-Host "Postgres host (default: localhost)"
        if (-not $hostname) { $hostname = 'localhost' }
        $port       = Read-Host "Port (default: 5432)"
        if (-not $port) { $port = '5432' }
        $username   = Read-Host "Superuser username (default: postgres)"
        if (-not $username) { $username = 'postgres' }
        $password   = Read-Host "Superuser password" -AsSecureString
        $dbName     = Read-Host "Database name (default: krakendeploy)"
        if (-not $dbName) { $dbName = 'krakendeploy' }

        $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
        $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)

        Write-Host ""
        Write-Host "Creating database..." -ForegroundColor Yellow
        dotnet run --project src/KrakenDeploy.Server -- database create `
            --host $hostname --port $port --username $username `
            --password $plainPassword --database-name $dbName

        $ConnectionString = "Host=$hostname;Port=$port;Database=$dbName;Username=$username;Password=$plainPassword"
    }
    else {
        $ConnectionString = Read-Host "Connection string"
    }
}

Write-Host ""
Write-Host "Running migrations and seeding data..." -ForegroundColor Yellow
dotnet run --project src/KrakenDeploy.Server -- database setup --connection-string "$ConnectionString"

Write-Host ""
Write-Host "Database setup complete." -ForegroundColor Green
Write-Host "Next: create an admin user with: dotnet run --project src/KrakenDeploy.Server -- users create-admin --email you@example.com --password ..."
