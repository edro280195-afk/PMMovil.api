# ⚙️ PMMovil - API Engine

![Version](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet)
![Database](https://img.shields.io/badge/MySQL-8.0-4479A1?style=for-the-badge&logo=mysql)
![SignalR](https://img.shields.io/badge/SignalR-Enabled-blue?style=for-the-badge&logo=microsoft)
![Status](https://img.shields.io/badge/Status-Active-success?style=for-the-badge)

## ✨ Overview

**PMMovil.Api** es el núcleo de procesamiento y sincronización para el ecosistema logístico de PMM. Esta API robusta gestiona la lógica de negocio, la persistencia de datos y la comunicación en tiempo real entre los diversos clientes (Angular, WebForms, dispositivos móviles).

---

## 🏗 Architecture & Technologies

- **Framework**: ASP.NET Core Web API.
- **Communication**: 
  - **RESTful Endpoints**: Gestión de revisiones, usuarios y permisos.
  - **SignalR Hubs**: Orquestación de eventos en tiempo real para el Dashboard de Revisadores.
- **Data Access**: Entity Framework Core con soporte para MySql.
- **Security**: Implementación de políticas de autenticación y autorización para flujos operativos críticos.

---

## 📦 Project Structure

- **Controllers**: Endpoints API estructurados por dominio (Revisiones, Usuarios, etc).
- **Services**: Capa de lógica de negocio desacoplada.
- **Data**: Contexto de base de datos y migraciones.
- **Hubs**: Implementaciones de SignalR para notificaciones push instantáneas.
- **Models**: DTOs y entidades de dominio con tipado fuerte.

---

## 🚀 Getting Started

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download)
- MySQL Server
- Visual Studio 2022 / VS Code

### Configuration

1. Clonar el repositorio:
   ```bash
   git clone https://github.com/edro280195-afk/PMMovil.api.git
   ```
2. Configurar la cadena de conexión en `appsettings.json`:
   ```json
   "ConnectionStrings": {
     "DefaultConnection": "Server=...;Database=...;User=...;Password=..."
   }
   ```
3. Restaurar paquetes y ejecutar:
   ```bash
   dotnet restore
   dotnet run
   ```

---

## 📄 License

Este proyecto es propiedad privada de **PMM**. Todos los derechos reservados.
