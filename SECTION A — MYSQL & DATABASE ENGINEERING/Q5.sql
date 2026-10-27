-- MySQL 8.0+. Optional filters use procedure parameters, so values are never
-- concatenated into SQL. Sort columns and direction are validated below.

CREATE TABLE Customers (
    Id INT PRIMARY KEY,
    Name VARCHAR(100) NOT NULL,
    Country CHAR(2) NOT NULL,
    City VARCHAR(50) NOT NULL
);

CREATE TABLE Orders (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    CustomerId INT NOT NULL,
    OrderDate DATETIME NOT NULL,
    Amount DECIMAL(12,2) NOT NULL,
    Status VARCHAR(20) NOT NULL,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE INDEX idx_orders_date_status ON Orders(OrderDate, Status, Amount);
CREATE INDEX idx_customers_country_city_name ON Customers(Country, City, Name);

DELIMITER $$
CREATE PROCEDURE SearchOrders(
    IN p_name VARCHAR(100),
    IN p_from DATE,
    IN p_to DATE,
    IN p_min DECIMAL(12,2),
    IN p_max DECIMAL(12,2),
    IN p_status VARCHAR(20),
    IN p_country CHAR(2),
    IN p_city VARCHAR(50),
    IN p_sortby VARCHAR(20),
    IN p_dir VARCHAR(4),
    IN p_page INT,
    IN p_size INT
)
BEGIN
    DECLARE v_sortby VARCHAR(20) DEFAULT 'OrderDate';
    DECLARE v_dir VARCHAR(4) DEFAULT 'DESC';
    DECLARE v_page INT DEFAULT 1;
    DECLARE v_size INT DEFAULT 20;
    DECLARE v_offset BIGINT UNSIGNED;

    SET v_sortby = COALESCE(p_sortby, v_sortby);
    SET v_dir = UPPER(COALESCE(p_dir, v_dir));
    SET v_page = COALESCE(p_page, v_page);
    SET v_size = COALESCE(p_size, v_size);

    IF v_sortby NOT IN ('CustomerName', 'Amount', 'Status', 'OrderDate') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Invalid SortBy value';
    END IF;
    IF v_dir NOT IN ('ASC', 'DESC') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Sort direction must be ASC or DESC';
    END IF;
    IF v_page < 1 OR v_size < 1 OR v_size > 100 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Page must be positive and PageSize must be 1 to 100';
    END IF;
    IF p_from IS NOT NULL AND p_to IS NOT NULL AND p_from > p_to THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DateFrom must not be later than DateTo';
    END IF;
    IF p_min IS NOT NULL AND p_max IS NOT NULL AND p_min > p_max THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MinAmount must not be greater than MaxAmount';
    END IF;

    SET v_offset = (CAST(v_page AS UNSIGNED) - 1) * CAST(v_size AS UNSIGNED);

    SELECT COUNT(*) AS Total
    FROM Orders o
    JOIN Customers c ON c.Id = o.CustomerId
    WHERE (p_name IS NULL OR c.Name LIKE CONCAT(p_name, '%'))
      AND (p_from IS NULL OR o.OrderDate >= p_from)
      AND (p_to IS NULL OR o.OrderDate < DATE_ADD(p_to, INTERVAL 1 DAY))
      AND (p_min IS NULL OR o.Amount >= p_min)
      AND (p_max IS NULL OR o.Amount <= p_max)
      AND (p_status IS NULL OR o.Status = p_status)
      AND (p_country IS NULL OR c.Country = p_country)
      AND (p_city IS NULL OR c.City = p_city);

    SELECT o.Id, c.Name, c.Country, c.City, o.OrderDate, o.Amount, o.Status
    FROM Orders o
    JOIN Customers c ON c.Id = o.CustomerId
    WHERE (p_name IS NULL OR c.Name LIKE CONCAT(p_name, '%'))
      AND (p_from IS NULL OR o.OrderDate >= p_from)
      AND (p_to IS NULL OR o.OrderDate < DATE_ADD(p_to, INTERVAL 1 DAY))
      AND (p_min IS NULL OR o.Amount >= p_min)
      AND (p_max IS NULL OR o.Amount <= p_max)
      AND (p_status IS NULL OR o.Status = p_status)
      AND (p_country IS NULL OR c.Country = p_country)
      AND (p_city IS NULL OR c.City = p_city)
    ORDER BY
        CASE WHEN v_sortby = 'CustomerName' AND v_dir = 'ASC' THEN c.Name END ASC,
        CASE WHEN v_sortby = 'CustomerName' AND v_dir = 'DESC' THEN c.Name END DESC,
        CASE WHEN v_sortby = 'Amount' AND v_dir = 'ASC' THEN o.Amount END ASC,
        CASE WHEN v_sortby = 'Amount' AND v_dir = 'DESC' THEN o.Amount END DESC,
        CASE WHEN v_sortby = 'Status' AND v_dir = 'ASC' THEN o.Status END ASC,
        CASE WHEN v_sortby = 'Status' AND v_dir = 'DESC' THEN o.Status END DESC,
        CASE WHEN v_sortby = 'OrderDate' AND v_dir = 'ASC' THEN o.OrderDate END ASC,
        CASE WHEN v_sortby = 'OrderDate' AND v_dir = 'DESC' THEN o.OrderDate END DESC,
        o.Id ASC
    LIMIT v_size OFFSET v_offset;
END$$
DELIMITER ;

-- Check actual plans for common searches with EXPLAIN ANALYZE. Optional-filter
-- predicates can affect index use, so keep only indexes that help real queries.
