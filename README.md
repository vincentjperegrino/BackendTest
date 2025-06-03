# Banking Event Receiver - Setup and Running Instructions

## Overview
This application is a production-quality event receiver that processes banking transactions (Credit/Debit) from Azure Service Bus messages and updates account balances in a SQL Server database. The system is designed to run in multiple containers simultaneously with proper concurrency handling and data integrity.

## Prerequisites

### Required Software
- **.NET 6.0 SDK or later**
- **SQL Server Express LocalDB** (or SQL Server instance)
- **Visual Studio 2022** or **VS Code** with C# extension
- **Azure Service Bus** namespace (for production) or **Azure Service Bus Emulator** (for local development) see [Azure Service Bus Emulator](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator?tabs=docker-linux-container) for setup instructions.
- **Docker Desktop** (optional, for containerized deployment)

### Development Tools (Recommended)
- **SQL Server Management Studio (SSMS)** or **Azure Data Studio**
- **Azure Service Bus Emulator** (for message management)
- **Postman** or similar (for testing)

## Initial Setup

### 1. Clone and Setup Repository
```bash
# Clone the repository
git clone <repository-url>
cd <file-location>

# Restore NuGet packages
dotnet restore
```

### 2. Database Configuration

#### Local Development (SQL Express LocalDB)
```bash
# Update connection string in appsettings.Development.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=BankingApiDb;Trusted_Connection=true;MultipleActiveResultSets=true;"
  }
}
```

#### Production/Docker
```bash
# Update connection string in appsettings.json or use environment variables
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost,1433;Database=BankingApiDb;User Id=sa;Password=YourPassword123!;TrustServerCertificate=true;"
  }
}
```

### 3. Database Migration
```bash
# Install EF Core tools (if not already installed)
dotnet tool install --global dotnet-ef

# Create and apply migrations
dotnet ef migrations add InitialCreate
dotnet ef database update
```

### 4. Azure Service Bus Configuration

#### For Local Development (Azure Service Bus Emulator)
```bash
# Start Emulator
docker compose -f <PathToDockerComposeFile> up -d
```

Update `appsettings.json`:
```json
{
"ConnectionStrings": {
    "ServiceBus": "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
  },
  "ServiceBus": {
    "QueueName": "banking-transactions-dev"
  }
}
```

#### For Production (Azure Service Bus)
Update `appsettings.json`:
```json
{
"ConnectionStrings": {
    "ServiceBus": "your-servicebus-connection-string",
  },
  "ServiceBus": {
    "QueueName": "banking-transactions"
  }
}
```

## Configuration Files

### appsettings.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "your-connection-string",
    "ServiceBus": "your-servicebus-connection-string",
  },
  "ServiceBus": {
    "QueueName": "banking-transactions"
  },
  "MessageProcessing": {
    "MaxRetryAttempts": 3,
    "RetryDelaySeconds": [5, 25, 125],
    "EmptyQueueDelaySeconds": 10
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

### appsettings.Development.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=BankingEventReceiver;Trusted_Connection=true;MultipleActiveResultSets=true;"
    "ServiceBus": "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
  },
  "ServiceBus": {
    "QueueName": "banking-transactions-dev"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```

## Running the Application

### Method 1: Visual Studio
1. Open the solution in Visual Studio 2022
2. Set the startup project to the main console application
3. Press F5 or click "Start Debugging"

### Method 2: Command Line
```bash
# Navigate to project directory
cd src/BankingEventReceiver

# Run the application
dotnet run

# Or run in watch mode for development
dotnet watch run
```

### Method 3: Docker (Production)

#### Build Docker Image
```bash
# Build the Docker image
docker build -t banking-event-receiver .

# Run with Docker Compose (recommended)
docker-compose up -d
```

#### Docker Compose Example
```yaml
version: '3.8'
services:
  app:
    build: .
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnection=Server=sql-server,1433;Database=BankingApiDb;User Id=sa;Password=YourPassword123!;TrustServerCertificate=true;
      - ServiceBus__ConnectionString=your-servicebus-connection-string
    depends_on:
      - sql-server
    restart: unless-stopped
    
  sql-server:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      - ACCEPT_EULA=Y
      - SA_PASSWORD=YourPassword123!
    ports:
      - "1433:1433"
    volumes:
      - sql-data:/var/opt/mssql
      
volumes:
  sql-data:
```

## Testing the Application

### 1. Seed Test Data
```sql
-- Insert test bank accounts
INSERT INTO BankAccounts (Id, AccountNumber, Balance, CreatedAt)
VALUES 
    ('7d445724-24ec-4d52-aa7a-ff2bac9f191d', 'ACC001', 1000.00, GETUTCDATE()),
    ('3bbaf4ca-5bfa-4922-a395-d755beac475f', 'ACC002', 500.00, GETUTCDATE());
```

### 2. Send Test Messages
Use Service Bus Explorer or create test messages:

**Credit Message:**
```json
{
  "id": "89479d8a-549b-41ea-9ccc-25a4106070a1",
  "messageType": "Credit",
  "bankAccountId": "7d445724-24ec-4d52-aa7a-ff2bac9f191d",
  "amount": 90.00
}
```

**Debit Message:**
```json
{
  "id": "89479d8a-549b-41ea-9ccc-25a4106070a2",
  "messageType": "Debit",
  "bankAccountId": "3bbaf4ca-5bfa-4922-a395-d755beac475f",
  "amount": 50.00
}
```

### 3. Monitor Application
- Check console output for processing logs
- Monitor database for balance updates
- Check Service Bus dead letter queue for failed messages

## Production Deployment

### Environment Variables
Set these environment variables in production:
```bash
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__DefaultConnection=your-production-db-connection
ServiceBus__ConnectionString=your-production-servicebus-connection
MessageProcessing__MaxRetryAttempts=3
```

### Common Issues

#### Database Connection Issues
```bash
# Check connection string
# Verify SQL Server is running
# Check firewall settings
```

#### Service Bus Connection Issues
```bash
# Verify connection string
# Check Azure Service Bus namespace status
# Validate queue exists
```

#### High Memory Usage
- Monitor for memory leaks
- Check database connection pooling
- Review message processing batch size

### Logs Location
- Console output (development)

## Development Guidelines

### Code Quality Standards
- Follow SOLID principles
- Implement proper error handling
- Use dependency injection
- Write unit and integration tests
- Follow async/await patterns