using Microsoft.EntityFrameworkCore;
using System;

namespace DBManager
{
    public class DbConnectionChecker
    {
        private readonly ApplicationDbContext _context;

        public DbConnectionChecker(ApplicationDbContext context)
        {
            _context = context;
        }

        public bool CheckConnection()
        {
            try
            {
                // Check if the database is reachable
                return _context.Database.CanConnect();
            }
            catch (Exception ex)
            {
                // Log or handle the error
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }
    }
}